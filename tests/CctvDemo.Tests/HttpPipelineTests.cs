using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

// Starts the real app in memory and talks to it over HTTP, so the middleware order, the headers and the
// cross-origin check are tested together, the way a browser meets them. (A header added for one reason once
// broke the sign-in form for another, and no unit test could see it.)
public class HttpPipelineTests : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "cctv-http-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Running : IDisposable
    {
        private readonly EnvScope env;
        private readonly WebApplicationFactory<Program> factory;
        public HttpClient Client { get; }

        public Running(string data, string? password)
        {
            env = new EnvScope(("CCTV_DATA_DIR", data), ("CCTV_UI_PASSWORD", password), ("CCTV_URL", null), ("CCTV_ALLOWED_HOSTS", null));
            factory = new WebApplicationFactory<Program>();
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }

        public void Dispose() { Client.Dispose(); factory.Dispose(); env.Dispose(); }
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return client.SendAsync(request);
    }

    [Theory]
    [InlineData("http://localhost", false)]          // the app's own pages
    [InlineData("null", true)]                       // what browsers send on a POST under Referrer-Policy: no-referrer
    [InlineData("https://evil.example", true)]       // another site
    public async Task Posts_from_the_apps_own_origin_pass_and_foreign_ones_are_rejected(string origin, bool rejected)
    {
        using var app = new Running(data, password: null);
        var response = await Post(app.Client, "/api/nothing", origin);
        Assert.Equal(rejected, response.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task With_a_password_a_same_origin_post_reaches_the_sign_in_check_and_a_foreign_one_is_still_rejected()
    {
        using var app = new Running(data, password: "test-password-123");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(app.Client, "/api/queue/pause", "http://localhost")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app.Client, "/api/queue/pause", "null")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app.Client, "/api/queue/pause", "https://evil.example")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers_and_a_referrer_policy_that_keeps_post_origins_intact()
    {
        using var app = new Running(data, password: null);
        var response = await app.Client.GetAsync("/api/health");

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        // "no-referrer" makes browsers send "Origin: null" on every POST, even to this same site,
        // and the cross-origin check then rejects the sign-in form and every other POST.
        Assert.NotEqual("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Health_answers_anyone_but_only_a_signed_in_user_sees_details()
    {
        using var app = new Running(data, password: "test-password-123");

        var health = await app.Client.GetAsync("/api/health");
        var body = await health.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"ok\":true", body);
        Assert.DoesNotContain("version", body);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Without_a_password_health_shows_its_details()
    {
        using var app = new Running(data, password: null);
        var body = await (await app.Client.GetAsync("/api/health")).Content.ReadAsStringAsync();
        Assert.Contains("version", body);
    }
}
