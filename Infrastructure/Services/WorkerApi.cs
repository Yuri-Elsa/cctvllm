using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// The coordinator's side of remote workers. A worker is another copy of this app (CCTV_COORDINATOR_URL set) on another
// machine: it asks for a video, downloads it, analyses it with its own CPU and model server, and sends back the result and
// the face-blurred copy. The coordinator keeps the queue, the review UI and the notifications; nothing else changes.
//
// Every call carries "Authorization: Bearer <CCTV_WORKER_TOKEN>" and "X-Worker: <name>". The endpoints are off unless
// CCTV_WORKER_TOKEN is set (at least 24 characters), and they do not use the browser sign-in.
public static partial class WorkerApi
{
    // A worker has to report in within this time or its video goes back to the queue.
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public static string? Token => Environment.GetEnvironmentVariable("CCTV_WORKER_TOKEN");
    public static bool Enabled => Token is { Length: >= 24 };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamePattern();

    public sealed record HeartbeatRequest(string? Stage);
    public sealed record FailRequest(string? Message);

    internal static bool TokenOk(string? header, string expected)
    {
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        var wanted = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(given, wanted);
    }

    private static string Worker(HttpContext http)
    {
        var name = http.Request.Headers["X-Worker"].ToString();
        if (!NamePattern().IsMatch(name)) throw new UserError("Send the worker's name in X-Worker (letters, digits, dot, dash, underscore).", 400);
        return name;
    }

    private static string? Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    public static void Map(WebApplication app)
    {
        var failures = new LoginThrottle();
        var group = app.MapGroup("/api/worker").AllowAnonymous();   // the bearer token replaces the sign-in
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var who = Address(http) ?? "unknown";
            if (failures.IsBlocked(who)) return Results.Json(new { detail = "Too many wrong tokens. Try again in a few minutes." }, statusCode: 429);
            if (!TokenOk(http.Request.Headers.Authorization.ToString(), Token!))
            {
                failures.Fail(who);
                return Results.Json(new { detail = "Wrong worker token." }, statusCode: 401);
            }
            failures.Reset(who);
            return await next(context);
        });

        group.MapPost("/claim", (HttpContext http, QueueStore store, WorkerRegistry registry, AuditLog audit) =>
        {
            var worker = Worker(http);
            var taken = store.TakeFor(worker, Lease);
            registry.Seen(worker, taken?.Job.Id);
            if (taken is null) return Results.NoContent();
            var (job, settings) = taken.Value;
            audit.Record("worker:" + worker, Address(http), "worker_claimed", job.Id, job.Name);
            // The worker keeps the video in a folder of its own, so it must not learn where the original lives here.
            var sent = job with { SourcePath = null, Worker = null, LeaseUntil = null, Stage = "Queued" };
            return Results.Json(new WorkerClaim(sent, settings, (int)Lease.TotalSeconds), QueueStore.Json);
        });

        group.MapGet("/jobs/{id}/video", (string id, HttpContext http, QueueStore store, WorkerRegistry registry) =>
        {
            var worker = Worker(http);
            var job = store.Holding(id, worker);
            var path = store.VideoPath(job);
            if (!File.Exists(path)) throw new UserError("The video file is missing on the coordinator.", 404);
            registry.Seen(worker, id);
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        group.MapPost("/jobs/{id}/heartbeat", (string id, HeartbeatRequest body, HttpContext http, QueueStore store, WorkerRegistry registry) =>
        {
            var worker = Worker(http);
            var stage = body.Stage is { Length: > 120 } text ? text[..120] : body.Stage;
            store.Lease(id, worker, Lease, stage);
            registry.Seen(worker, id);
            return Results.Ok(new { ok = true });
        });

        // The blurred copy and its poster, uploaded before /complete.
        group.MapPut("/jobs/{id}/files/{name}", async (string id, string name, HttpRequest request, QueueStore store, CancellationToken ct) =>
        {
            var worker = Worker(request.HttpContext);
            store.Holding(id, worker);
            if (name is not (FaceBlur.ClipName or "poster.jpg")) throw new UserError("Unknown file.", 400);
            var cap = name == "poster.jpg" ? 8L * 1024 * 1024 : VideoTools.MaxBytes * 2;
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = cap;
            var target = Path.Combine(store.Folder(id), name);
            var temp = target + ".upload";
            long bytes = 0;
            try
            {
                await using (var file = File.Create(temp))
                {
                    var buffer = new byte[81920]; int n;
                    while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
                    {
                        bytes += n;
                        if (bytes > cap) throw new UserError("That file is too large.", 413);
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                if (bytes == 0) throw new UserError("Empty file.", 400);
                store.Holding(id, worker);   // it may have been cancelled during the upload
                File.Move(temp, target, true);
            }
            catch { try { File.Delete(temp); } catch (IOException) { } throw; }
            return Results.Ok(new { bytes });
        });

        group.MapPost("/jobs/{id}/complete", (string id, WorkerReport report, HttpContext http, QueueStore store, DescriptionWorker pipeline,
            IHostApplicationLifetime lifetime, ILoggerFactory logs, AuditLog audit) =>
        {
            var worker = Worker(http);
            var job = store.Holding(id, worker);
            if (report.Result is null) throw new UserError("The report has no result.", 400);
            var config = store.Settings;
            var blur = new BlurOutcome(report.BlurRequested, report.BlurDone, report.BlurError);
            if (blur.Done && !File.Exists(FaceBlur.ClipPath(store.Folder(id))))
                blur = blur with { Done = false, Error = "The worker did not upload the blurred video." };
            // Record the result before answering; notifying can take a while (the agent may call the model), so it runs on its own.
            pipeline.CommitFromWorker(id, worker, config, report.Result, report.ElapsedSeconds, blur);
            audit.Record("worker:" + worker, Address(http), "worker_completed", id, job.Name);
            var logger = logs.CreateLogger("WorkerApi");
            _ = Task.Run(async () =>
            {
                try { await pipeline.Announce(id, config, canReanalyze: DescriptionWorker.LocalWorkers > 0, report.ElapsedSeconds, lifetime.ApplicationStopping); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Notifying about {Video} failed.", job.Name); }
            });
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/jobs/{id}/fail", (string id, FailRequest body, HttpContext http, QueueStore store, DescriptionWorker pipeline, AuditLog audit) =>
        {
            var worker = Worker(http);
            var job = store.Holding(id, worker);
            pipeline.Fail(id, string.IsNullOrWhiteSpace(body.Message) ? "The worker could not analyse this video." : $"[{worker}] {body.Message}");
            audit.Record("worker:" + worker, Address(http), "worker_failed", id, job.Name);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/jobs/{id}/release", (string id, HttpContext http, QueueStore store) =>
        {
            store.Release(id, Worker(http));
            return Results.Ok(new { ok = true });
        });
    }
}
