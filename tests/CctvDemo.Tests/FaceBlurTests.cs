using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Face blur decides which file a viewer gets. Everything that must never show a face is tested here:
// the choice itself, the HTTP endpoint, and the settings that turn blur on.
public class FaceBlurTests : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "cctv-blur-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static VideoJob Job(Func<VideoJob, VideoJob>? change = null)
    {
        var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = "clip.mp4", Status = "completed" };
        return change is null ? job : change(job);
    }

    [Fact]
    public void Blur_is_on_by_default_and_old_settings_load_with_it_on()
    {
        Assert.True(new ModelSettings().BlurFaces);
        var old = JsonSerializer.Deserialize<ModelSettings>("""{"mode":"local"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(old.BlurFaces);
        var off = JsonSerializer.Deserialize<ModelSettings>("""{"mode":"local","blurFaces":false}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(off.BlurFaces);
    }

    [Fact]
    public void A_blurred_copy_is_what_viewers_get()
    {
        var job = Job(j => j with { BlurFaces = true, FacesBlurred = true });
        Assert.Equal(FaceBlur.ClipChoice.Blurred, FaceBlur.Choose(job, wantsOriginal: false));
    }

    [Fact]
    public void A_failed_blur_withholds_the_video_instead_of_showing_faces()
    {
        var job = Job(j => j with { BlurFaces = true, FacesBlurred = false, BlurError = "boom" });
        Assert.Equal(FaceBlur.ClipChoice.Withheld, FaceBlur.Choose(job, wantsOriginal: false));
    }

    [Theory]
    [InlineData(false, false, false, "completed", FaceBlur.ClipChoice.Original)]   // processed with blur off
    [InlineData(true, false, true, "completed", FaceBlur.ClipChoice.Original)]     // nobody in it: nothing to blur
    [InlineData(true, false, false, "queued", FaceBlur.ClipChoice.Original)]       // not processed yet
    [InlineData(true, false, false, "processing", FaceBlur.ClipChoice.Original)]
    [InlineData(true, false, false, "completed", FaceBlur.ClipChoice.Withheld)]
    public void Only_a_processed_video_with_people_and_no_copy_is_withheld(bool blurFaces, bool blurred, bool noPeople, string status, FaceBlur.ClipChoice expected)
    {
        var job = Job(j => j with { BlurFaces = blurFaces, FacesBlurred = blurred, NoPeople = noPeople, Status = status });
        Assert.Equal(expected, FaceBlur.Choose(job, wantsOriginal: false));
    }

    [Fact]
    public void An_explicit_request_gets_the_original_even_when_a_copy_exists()
    {
        var job = Job(j => j with { BlurFaces = true, FacesBlurred = true });
        Assert.Equal(FaceBlur.ClipChoice.Original, FaceBlur.Choose(job, wantsOriginal: true));
    }

    [Fact]
    public void The_clip_lives_in_the_jobs_own_folder()
    {
        Assert.Equal(Path.Combine("x", "video-blurred.mp4"), FaceBlur.ClipPath("x"));
    }

    [Fact]
    public void A_custom_face_model_comes_from_the_environment()
    {
        using (new EnvScope(("CCTV_FACE_MODEL", null))) Assert.Null(FaceBlur.ModelOverride);
        using (new EnvScope(("CCTV_FACE_MODEL", "/models/face.onnx"))) Assert.Equal("/models/face.onnx", FaceBlur.ModelOverride);
    }

    // --- over HTTP

    private sealed class Running : IDisposable
    {
        private readonly EnvScope env;
        private readonly WebApplicationFactory<Program> factory;
        public HttpClient Client { get; }
        public QueueStore Store { get; }

        public Running(string data, string? password)
        {
            env = new EnvScope(("CCTV_DATA_DIR", data), ("CCTV_UI_PASSWORD", password), ("CCTV_URL", null), ("CCTV_ALLOWED_HOSTS", null), ("CCTV_USERS_FILE", null));
            factory = new WebApplicationFactory<Program>();
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Store = factory.Services.GetRequiredService<QueueStore>();
        }

        public void Dispose() { Client.Dispose(); factory.Dispose(); env.Dispose(); }
    }

    // A job whose original and blurred files hold different bytes, so a response says which one it served.
    private static VideoJob AddJob(QueueStore store, Func<VideoJob, VideoJob> change)
    {
        var job = change(new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = "clip.mp4", Status = "completed" });
        var folder = store.Folder(job.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "video"), "ORIGINAL"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "video-blurred.mp4"), "BLURRED"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "poster.jpg"), new byte[] { 0xFF, 0xD8, 0xFF });
        store.Add(job);
        return job;
    }

    [Fact]
    public async Task The_endpoint_serves_the_blurred_copy_and_the_original_only_when_asked_for()
    {
        using var app = new Running(data, password: null);
        var job = AddJob(app.Store, j => j with { BlurFaces = true, FacesBlurred = true });

        var normal = await app.Client.GetAsync($"/api/videos/{job.Id}/video");
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        Assert.Equal("BLURRED", await normal.Content.ReadAsStringAsync());
        Assert.Equal("video/mp4", normal.Content.Headers.ContentType?.MediaType);

        var original = await app.Client.GetAsync($"/api/videos/{job.Id}/video?original=1");
        Assert.Equal("ORIGINAL", await original.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_withheld_video_answers_409_and_never_sends_the_original()
    {
        using var app = new Running(data, password: null);
        var job = AddJob(app.Store, j => j with { BlurFaces = true, FacesBlurred = false, BlurError = "no ffmpeg" });

        var response = await app.Client.GetAsync($"/api/videos/{job.Id}/video");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("ORIGINAL", body);
        Assert.Contains("administrator", body);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.GetAsync($"/api/videos/{job.Id}/poster.jpg")).StatusCode);
    }

    [Fact]
    public async Task A_missing_blurred_file_is_an_error_not_a_quiet_fallback_to_the_original()
    {
        using var app = new Running(data, password: null);
        var job = AddJob(app.Store, j => j with { BlurFaces = true, FacesBlurred = true });
        File.Delete(Path.Combine(app.Store.Folder(job.Id), "video-blurred.mp4"));

        var response = await app.Client.GetAsync($"/api/videos/{job.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("ORIGINAL", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Videos_processed_without_blur_are_served_as_before()
    {
        using var app = new Running(data, password: null);
        var job = AddJob(app.Store, j => j with { BlurFaces = false });
        Assert.Equal("ORIGINAL", await (await app.Client.GetAsync($"/api/videos/{job.Id}/video")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_sign_in_nobody_reaches_the_original_when_a_password_is_set()
    {
        using var app = new Running(data, password: "test-password-123");
        var job = AddJob(app.Store, j => j with { BlurFaces = true, FacesBlurred = true });
        var response = await app.Client.GetAsync($"/api/videos/{job.Id}/video?original=1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Settings_save_the_blur_switch()
    {
        using var app = new Running(data, password: null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings")
        {
            Content = new StringContent("""{"mode":"local","endpoint":"http://127.0.0.1:11434/v1","model":"m","blurFaces":false}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request)).StatusCode);
        Assert.False(app.Store.Settings.BlurFaces);
    }
}
