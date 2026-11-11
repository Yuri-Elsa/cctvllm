using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// A worker machine. Started instead of the normal queue worker when CCTV_COORDINATOR_URL is set: it asks the coordinator
// for a video, downloads it, runs the same analysis the coordinator would (person tracking, the model, face blur) with this
// machine's CPU and model server, and sends back the result. It reports in every few seconds; if it stops (crash, network),
// the coordinator queues the video again for another worker, and if it is told the video is no longer its own (cancelled,
// removed) it stops at once.
//
//   CCTV_COORDINATOR_URL   https://cctv.example.com   (https, or a private IP address)
//   CCTV_WORKER_TOKEN      the same secret as on the coordinator (at least 24 characters)
//   CCTV_WORKER_NAME       name shown on the coordinator (default: the machine name)
//   CCTV_WORKERS           videos analysed at the same time on this machine (default 1)
//   CCTV_WORKER_ENDPOINTS  model servers for this worker, comma separated (default: the ones in the coordinator's Settings,
//                          where 127.0.0.1 means this machine's own Ollama)
//   CCTV_WORKER_MODEL      model name on this machine, when it differs from the coordinator's Settings
public sealed class RemoteWorker(DescriptionWorker pipeline, QueueStore store, ILogger<RemoteWorker> logger) : BackgroundService
{
    public static string? CoordinatorUrl =>
        Environment.GetEnvironmentVariable("CCTV_COORDINATOR_URL") is { Length: > 0 } url ? url.Trim().TrimEnd('/') : null;
    public static bool Enabled => CoordinatorUrl is not null;
    public static int Loops => int.TryParse(Environment.GetEnvironmentVariable("CCTV_WORKERS"), out var n) ? Math.Clamp(n, 1, 8) : 1;

    private static string BaseName
    {
        get
        {
            var name = Environment.GetEnvironmentVariable("CCTV_WORKER_NAME") is { Length: > 0 } given ? given : Environment.MachineName;
            var clean = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
            return clean.Length == 0 ? "worker" : clean[..Math.Min(clean.Length, 56)];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (CoordinatorUrl is not { } url) return;
        if (RemoteHost.CheckCoordinator(url) is { } problem) { logger.LogError("Worker not started: {Problem}", problem); return; }
        if (WorkerApi.Token is not { Length: >= 24 }) { logger.LogError("Worker not started: set CCTV_WORKER_TOKEN to the coordinator's token (at least 24 characters)."); return; }
        // Whatever an earlier run left behind belongs to jobs the coordinator has long since given to someone else.
        store.RemoveMany(store.Jobs().Select(j => j.Id));
        var names = Loops == 1 ? new[] { BaseName } : Enumerable.Range(1, Loops).Select(i => $"{BaseName}-{i}").ToArray();
        logger.LogInformation("Worker {Names} for {Coordinator}", string.Join(", ", names), url);
        await Task.WhenAll(names.Select(name => Task.Run(() => Loop(name, url, stoppingToken), stoppingToken)));
    }

    private static HttpClient Create(string name, string url)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };   // never follow a redirect with the token
        var http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(url + "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkerApi.Token);
        http.DefaultRequestHeaders.Add("X-Worker", name);
        return http;
    }

    private async Task Loop(string name, string url, CancellationToken stop)
    {
        using var http = Create(name, url);
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var claim = await Claim(http, stop);
                failures = 0;
                if (claim is null) { await Task.Delay(2000, stop); continue; }
                await Run(http, name, claim, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1 || failures % 12 == 0)
                    logger.LogWarning("Worker {Name}: cannot get work from the coordinator ({Message}). Retrying.", name, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // A request with its own time limit, on top of the shutdown token.
    private static async Task<HttpResponseMessage> Call(HttpClient http, HttpRequestMessage request, int seconds, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        return await http.SendAsync(request, completion, limit.Token);
    }

    private static HttpRequestMessage Post(string path, object? body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }, options: QueueStore.Json) };

    private static async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync();
        string detail;
        try { detail = JsonDocument.Parse(text).RootElement.GetProperty("detail").GetString() ?? text; } catch (Exception) { detail = text; }
        throw new HttpRequestException($"Coordinator answered {(int)response.StatusCode}: {(detail.Length > 200 ? detail[..200] : detail)}", null, response.StatusCode);
    }

    private async Task<WorkerClaim?> Claim(HttpClient http, CancellationToken ct)
    {
        using var response = await Call(http, Post("api/worker/claim", null), 30, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await Check(response);
        return JsonSerializer.Deserialize<WorkerClaim>(await response.Content.ReadAsStringAsync(ct), QueueStore.Json);
    }

    // This worker's own model servers, when it has any (the coordinator's 127.0.0.1 would otherwise mean this machine's Ollama).
    internal static ModelSettings Override(ModelSettings config)
    {
        if (Environment.GetEnvironmentVariable("CCTV_WORKER_ENDPOINTS") is { Length: > 0 } list && ModelEndpoints.Parse(null, list) is { Count: > 0 } endpoints)
            config = config with { Endpoint = endpoints[0], ExtraEndpoints = string.Join("\n", endpoints.Skip(1)) };
        if (Environment.GetEnvironmentVariable("CCTV_WORKER_MODEL") is { Length: > 0 } model)
            config = config with { Model = model.Trim() };
        return config;
    }

    private async Task Run(HttpClient http, string name, WorkerClaim claim, CancellationToken stop)
    {
        var job = claim.Job with { Status = "processing", Stage = "Downloading video", SourcePath = null, Worker = null, LeaseUntil = null };
        var config = Override(claim.Settings);
        Directory.CreateDirectory(store.Folder(job.Id));
        store.Add(job);
        using var jobToken = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var lost = false;
        var lease = TimeSpan.FromSeconds(Math.Max(9, claim.LeaseSeconds));
        var heartbeat = Heartbeat(http, job.Id, lease, () => { lost = true; jobToken.Cancel(); }, jobToken.Token);
        var watch = Stopwatch.StartNew();
        logger.LogInformation("Worker {Name}: {Video} ({Id})", name, job.Name, job.Id);
        try
        {
            await Download(http, job, store.VideoPath(job), jobToken.Token);
            store.Update(job.Id, j => j with { Stage = "Extracting frames" });
            var (result, blur) = await pipeline.Analyze(store.Get(job.Id), config, jobToken.Token);
            if (blur.Done)
            {
                var folder = store.Folder(job.Id);
                await Upload(http, job.Id, "poster.jpg", Path.Combine(folder, "poster.jpg"), jobToken.Token);
                await Upload(http, job.Id, FaceBlur.ClipName, FaceBlur.ClipPath(folder), jobToken.Token);
            }
            store.Update(job.Id, j => j with { Stage = "Sending result" });
            var report = new WorkerReport
            {
                Result = result, BlurRequested = blur.Requested, BlurDone = blur.Done, BlurError = blur.Error, ElapsedSeconds = watch.Elapsed.TotalSeconds
            };
            using var response = await Call(http, Post($"api/worker/jobs/{job.Id}/complete", report), 60, jobToken.Token);
            await Check(response);
            logger.LogInformation("Worker {Name}: finished {Video} in {Seconds:0}s", name, job.Name, watch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            if (lost) logger.LogInformation("Worker {Name}: {Video} is no longer this worker's (cancelled, removed or given to another worker). Stopped.", name, job.Name);
            else await Try(http, $"api/worker/jobs/{job.Id}/release", null);   // shutting down: hand the video back
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogInformation("Worker {Name}: {Video} is no longer this worker's. Dropped.", name, job.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Worker {Name}: {Video} failed", name, job.Name);
            await Try(http, $"api/worker/jobs/{job.Id}/fail", new { message = DescriptionWorker.Explain(ex) });
        }
        finally
        {
            jobToken.Cancel();
            try { await heartbeat; } catch (Exception) { }
            Discard(job.Id);
        }
    }

    // Best effort, with a short limit of its own: used when the main token is already cancelled.
    private async Task Try(HttpClient http, string path, object? body)
    {
        try { using var response = await Call(http, Post(path, body), 10, CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Could not tell the coordinator ({Message}); its lease will run out and the video will be queued again.", ex.Message);
        }
    }

    private async Task Heartbeat(HttpClient http, string id, TimeSpan lease, Action lost, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(3, lease.TotalSeconds / 3));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { return; }
            try
            {
                var stage = store.Jobs().FirstOrDefault(j => j.Id == id)?.Stage;
                using var response = await Call(http, Post($"api/worker/jobs/{id}/heartbeat", new { stage }), 20, ct);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) { lost(); return; }
            }
            catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                // The coordinator is briefly unreachable. Keep working: the lease leaves room for a few missed beats.
            }
        }
    }

    private static async Task Download(HttpClient http, VideoJob job, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/worker/jobs/{job.Id}/video");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(response);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
            await source.CopyToAsync(file, 81920, ct);
        var received = new FileInfo(path).Length;
        if (received == 0 || (job.Bytes > 0 && received != job.Bytes))
            throw new UserError($"The video did not download completely ({received} of {job.Bytes} bytes).", 502);
    }

    private static async Task Upload(HttpClient http, string id, string name, string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/worker/jobs/{id}/files/{name}") { Content = new StreamContent(file) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await Check(response);
    }

    // The video is the coordinator's: nothing of it stays on this machine.
    private void Discard(string id)
    {
        try { store.Update(id, j => j with { Status = "cancelled" }); store.Remove(id); }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException) { }
    }
}
