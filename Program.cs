using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var auth = new AuthOptions();
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CCTV_URL") ?? "http://127.0.0.1:8766");
// Ordinary requests (settings, folder, review) are small JSON. Only the upload endpoint raises its own limit.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
// Behind a reverse proxy (HTTPS termination) set CCTV_TRUST_PROXY=1 so the real scheme, host and client
// address are used: the Secure cookie flag, the login throttle and the origin check all depend on them.
// Only enable it when the app is reachable through the proxy alone, otherwise clients could spoof the headers.
var trustProxy = Environment.GetEnvironmentVariable("CCTV_TRUST_PROXY") is "1" or "true";
if (trustProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o => {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
    });
builder.Services.AddControllersWithViews().AddRazorOptions(options => {
    options.ViewLocationFormats.Clear();
    options.ViewLocationFormats.Add("/Web/Views/{1}/{0}.cshtml");
    options.ViewLocationFormats.Add("/Web/Views/Shared/{0}.cshtml");
});
if (auth.Enabled)
{
    var passwordVersion = auth.Version;
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
        o.Cookie.Name = "cctv_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure once the request is HTTPS (see CCTV_TRUST_PROXY)
        o.LoginPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnValidatePrincipal = ctx => {
            if (ctx.Principal?.FindFirst("pwv")?.Value == passwordVersion) return Task.CompletedTask;
            ctx.RejectPrincipal();
            return ctx.HttpContext.SignOutAsync();
        };
    });
}
builder.Services.AddAuthorization(o => { if (auth.Enabled) o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddSingleton<VideoTools>();
builder.Services.AddOptions<WatchOptions>();
builder.Services.AddSingleton<IVideoIngest, VideoIngestService>();
builder.Services.AddOptions<RtspOptions>();
builder.Services.AddSingleton<RtspIngestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtspIngestService>());
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddHostedService<FolderWatcherService>();
builder.Services.AddOptions<RetentionOptions>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHttpClient("model", c => c.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddOptions<NotificationOptions>();
builder.Services.AddHttpClient<WebhookNotifier>();
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
builder.Services.AddOptions<WhatsAppOptions>();
builder.Services.AddHttpClient<WhatsAppNotifier>();
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<WhatsAppNotifier>());
builder.Services.AddSingleton<INotifier, EmailNotifier>();
builder.Services.AddSingleton<INotifier, LogNotifier>();
builder.Services.AddSingleton<INotificationResultSink, QueueNotificationSink>();
builder.Services.AddSingleton(sp => new AlertJournal(
    Path.Combine(sp.GetRequiredService<QueueStore>().Root, "pending-alerts.json"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AlertJournal>()));
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
builder.Services.AddOptions<WatchdogOptions>();
builder.Services.AddHostedService<CameraWatchdog>();
builder.Services.AddSingleton<Preflight>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Preflight>());
builder.Services.AddSingleton<INotificationAgent, LlmNotificationAgent>();
builder.Services.AddSingleton<FlagNotifier>();
builder.Services.AddSingleton<DescriptionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DescriptionWorker>());
var app = builder.Build();
if (trustProxy) app.UseForwardedHeaders();
var allowedHosts = HostGuard.Allowed(Environment.GetEnvironmentVariable("CCTV_URL"), Environment.GetEnvironmentVariable("CCTV_ALLOWED_HOSTS"));
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // Stops other sites from framing the UI (clickjacking) and limits what the pages may load.
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    // Not "no-referrer": with that policy browsers send "Origin: null" on every POST, even to this same site,
    // and the cross-origin check below would reject the login form and every other POST.
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "img-src 'self' data:; media-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (!HostGuard.IsAllowed(ctx.Request.Host.Value, allowedHosts))
    {
        ctx.Response.StatusCode = 421; await ctx.Response.WriteAsJsonAsync(new { detail = "Unexpected Host header. Set CCTV_ALLOWED_HOSTS to the name you use to open the app." }); return;
    }
    if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD" &&
        ctx.Request.Headers.Origin.Count > 0 && !HostGuard.SameOrigin(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host.Value))
    {
        ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { detail = "Cross-origin requests are not allowed." }); return;
    }
    try { await next(ctx); }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex is UserError u ? u.Status : ex is BadHttpRequestException b ? b.StatusCode : 500;
        await ctx.Response.WriteAsJsonAsync(new { detail = ex is UserError or BadHttpRequestException ? ex.Message : "Request failed. Check the server log." });
        if (ex is not UserError) app.Logger.LogError(ex, "Request failed");
    }
});
app.UseStaticFiles();
if (auth.Enabled) { app.UseAuthentication(); app.UseAuthorization(); }
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

// The browser's folder picker only ever reports a folder *name*, never its path, so a linked
// folder is entered as text and read by the server. That is the whole point of the feature:
// this server runs on the same machine as the videos, so it can re-read the folder later on
// its own -- a picker selection is a one-time snapshot that cannot be refreshed.
string ResolveFolder(string raw)
{
    var value = raw.Trim();
    if (value.StartsWith('~'))
        value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..].TrimStart('/'));
    if (!Path.IsPathRooted(value)) throw new UserError("Enter the folder's full path, starting with / or ~.");
    // A network path makes Windows contact that server (and offer the user's credentials) just to check it exists.
    if (OperatingSystem.IsWindows() && (value.StartsWith(@"\\") || value.StartsWith("//")) &&
        Environment.GetEnvironmentVariable("CCTV_ALLOW_UNC") != "1")
        throw new UserError("Network paths (\\\\server\\share) are blocked. Set CCTV_ALLOW_UNC=1 if you need one.");
    var full = Path.GetFullPath(value);
    if (!Directory.Exists(full)) throw new UserError("There is no folder at that path.", 404);
    // Follow a link so that the "app's own data folder" check below sees where it really points.
    return new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
}

app.MapGet("/api/state", (QueueStore s) => s.Snapshot());
app.MapGet("/api/cameras", (RtspIngestService r) => r.Snapshot());
// preflight is the last startup check's overall result ("ok", "warn", "fail"), or null while the first one is still running.
// Open to anyone so a monitor can poll it without logging in; only a signed-in user (or an app without a password) sees details.
app.MapGet("/api/health", (Preflight p, HttpContext http) =>
    !auth.Enabled || http.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new { ok = true, backend = "ASP.NET Core", version = Environment.Version.ToString(), preflight = p.Latest?.Overall })
        : Results.Ok(new { ok = true })).AllowAnonymous();
// Full setup report. ?refresh=true runs the checks again (the "Run check" button in Settings).
app.MapGet("/api/preflight", (bool? refresh, Preflight p, CancellationToken ct) => p.GetAsync(refresh == true, ct));
app.MapGet("/api/models", async (string? endpoint, IHttpClientFactory clients, CancellationToken ct) => {
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https") || !uri.IsLoopback ||
        !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        throw new UserError("Enter a localhost model server URL.", 400);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    try
    {
        using var client = clients.CreateClient("model");
        using var response = await client.GetAsync(endpoint!.TrimEnd('/') + "/models", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new UserError("Could not list models from this server.", 502);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var models = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct().Order().ToArray();
        return Results.Ok(new { models });
    }
    catch (HttpRequestException)
    {
        throw new UserError("Cannot reach the model server. Start Ollama and try again.", 502);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        throw new UserError("Model list request timed out. Try again.", 504);
    }
});
app.MapPost("/api/settings", (ModelSettings value, QueueStore s) => {
    if (value.Mode is not ("local" or "notebook")) throw new UserError("Choose local model or notebook.");
    if (ModelEndpointPolicy.Check(value.Mode, value.Endpoint) is { } endpointProblem) throw new UserError(endpointProblem);
    if (value.Mode == "local" && string.IsNullOrWhiteSpace(value.Model)) throw new UserError("Enter a model name.");
    if (value.PersonDetector is not ("yolo" or "llm")) throw new UserError("Choose YOLO or LLM for person detection.");
    if (!(value.PersonThreshold is >= 0 and <= 1) || !(value.SuspiciousThreshold is >= 0 and <= 1))
        throw new UserError("Thresholds must be between 0 and 1.");
    if ((value.FlagActivity?.Length ?? 0) > 4000 || (value.SiteContext?.Length ?? 0) > 4000)
        throw new UserError("Criteria and context must each be 4,000 characters or less.");
    s.SetSettings(value with { Endpoint = value.Endpoint.TrimEnd('/'), Model = (value.Model ?? "").Trim() });
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/videos", async (HttpRequest request, QueueStore s, VideoTools videos, CancellationToken ct) => {
    if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = VideoTools.MaxBytes;
    // Uploads are the only thing the retention service never cleans up, so give them a ceiling of their own.
    var stored = s.Jobs().Where(j => j.SourcePath is null).Sum(j => j.Bytes);
    if (stored >= VideoTools.MaxUploadBytes)
        throw new UserError("Upload storage is full. Remove some videos first (limit: CCTV_MAX_UPLOAD_GB).", 507);
    var name = Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-Filename"].FirstOrDefault() ?? "video.mp4"));
    if (name.Length > 200) name = name[..200];
    var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = name };
    var dir = s.Folder(job.Id); Directory.CreateDirectory(dir);
    try
    {
        long bytes = 0;
        await using (var file = File.Create(Path.Combine(dir, "video")))
        {
            var buffer = new byte[81920]; int n;
            while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                bytes += n; if (bytes > VideoTools.MaxBytes) throw new UserError("Maximum file size is 500 MB.", 413);
                if (stored + bytes > VideoTools.MaxUploadBytes) throw new UserError("Upload storage is full. Remove some videos first (limit: CCTV_MAX_UPLOAD_GB).", 507);
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        var info = await videos.Inspect(Path.Combine(dir, "video"), ct);
        job = job with { Duration = info.Duration, Fps = info.Fps, Bytes = bytes };
        await videos.Poster(Path.Combine(dir, "video"), Path.Combine(dir, "poster.jpg"), ct);
        s.Add(job); return Results.Ok(job);
    }
    catch { Directory.Delete(dir, true); throw; }
});
// Linking a folder only saves the path and asks FolderWatcherService to import it. The call returns
// at once; progress and the result come from GET /api/folder/status.
app.MapPost("/api/folder", (LinkRequest value, QueueStore s, ScanCoordinator scans) => {
    if (string.IsNullOrWhiteSpace(value.Path))
    {
        s.SetLinkedFolder(null);
        return Results.Ok(new { linkedFolder = (string?)null, importing = false });
    }
    var folder = ResolveFolder(value.Path);
    if (folder.Equals(s.Root, StringComparison.OrdinalIgnoreCase) ||
        folder.StartsWith(s.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new UserError("That is the app's own data folder. Choose the folder your videos are in.");
    s.SetLinkedFolder(folder);
    scans.Request();
    return Results.Ok(new { linkedFolder = folder, importing = true });
});
app.MapPost("/api/folder/refresh", (QueueStore s, ScanCoordinator scans) => {
    if (s.LinkedFolder is null) throw new UserError("Link a folder first.");
    scans.Request();
    return Results.Ok(new { importing = true });
});
app.MapGet("/api/folder/status", (ScanCoordinator scans) => scans.Status());
app.MapGet("/api/videos/{id}/{asset}", (string id, string asset, QueueStore s, HttpContext http) => {
    var job = s.Get(id);
    if (asset is not ("video" or "poster.jpg")) return Results.NotFound();
    var path = asset == "video" ? s.VideoPath(job) : Path.Combine(s.Folder(job.Id), asset);
    if (!File.Exists(path))
        throw new UserError(asset == "video" && job.SourcePath is not null
            ? "This video is no longer at its original path in the linked folder." : "File not found.", 404);
    var type = "image/jpeg";
    if (asset == "video")
    {
        // The name comes from the uploader, so never let it pick html/svg/js: only video/* is served.
        if (!new FileExtensionContentTypeProvider().TryGetContentType(job.Name, out type) || !type.StartsWith("video/", StringComparison.Ordinal))
            type = "video/mp4";
    }
    http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
    return Results.File(path, type, enableRangeProcessing: true);
});
app.MapPost("/api/notifications/test", async (NotificationDispatcher d, CancellationToken ct) =>
    Results.Ok(new { results = await d.SendTestAsync(ct) }));
app.MapGet("/api/export", (string? format, string? camera, bool? flagged, QueueStore s) => {
    format = (format ?? "csv").ToLowerInvariant();
    if (format is not ("csv" or "json")) throw new UserError("Format must be csv or json.");
    var jobs = JobExporter.Select(s.Jobs(), camera, flagged == true).ToList();
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return format == "csv"
        ? Results.File(JobExporter.ToCsv(jobs), "text/csv; charset=utf-8", $"cctv-jobs-{stamp}.csv")
        : Results.File(JobExporter.ToJson(jobs, DateTimeOffset.UtcNow), "application/json", $"cctv-jobs-{stamp}.json");
});
app.MapPost("/api/queue/{action}", (string action, QueueStore s) => {
    if (action is not ("start" or "pause")) throw new UserError("Unknown queue action.");
    s.SetRunning(action == "start"); return Results.Ok(new { ok = true });
});
app.MapPost("/api/videos/{id}/review", (string id, ReviewRequest value, QueueStore s) => {
    s.SetReview(id, value.State);
    return Results.Ok(new { ok = true, reviewState = s.Get(id).ReviewState });
});
app.MapPost("/api/videos/{id}/{action}", (string id, string action, QueueStore s, DescriptionWorker worker) => {
    if (action == "cancel") worker.Cancel(id);
    else if (action == "retry") s.Retry(id);
    else if (action == "delete") s.Remove(id);
    else throw new UserError("Unknown video action.");
    return Results.Ok(new { ok = true });
});
app.Run();