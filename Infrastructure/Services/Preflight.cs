using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

public sealed record PreflightCheck(string Name, string Status, string Detail);

public sealed record PreflightReport(DateTimeOffset CheckedAt, string Overall, IReadOnlyList<PreflightCheck> Checks);

public sealed class Preflight(QueueStore store, VideoTools videos, RtspIngestService rtsp, IEnumerable<INotifier> notifiers, WorkerRegistry workers,
    IHttpClientFactory clients, IWebHostEnvironment env, UserStore users, ILogger<Preflight> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile PreflightReport? latest;

    public PreflightReport? Latest => latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var report = await GetAsync(refresh: true, stoppingToken);
            foreach (var check in report.Checks)
            {
                var level = check.Status switch { "fail" => LogLevel.Error, "warn" => LogLevel.Warning, _ => LogLevel.Information };
                logger.Log(level, "Preflight {Status,-4} {Check}: {Detail}", check.Status.ToUpperInvariant(), check.Name, check.Detail);
            }
            logger.Log(report.Overall == "ok" ? LogLevel.Information : LogLevel.Warning,
                "Preflight finished: {Overall}. Details: GET /api/preflight or Settings > System check.", report.Overall);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Preflight could not run."); }
    }

    // refresh=false returns the last report when there is one. Runs never overlap.
    public async Task<PreflightReport> GetAsync(bool refresh, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && latest is { } cached) return cached;
            var checks = await Task.WhenAll(
                Guard("FFmpeg", () => CheckFfmpeg(ct)),
                Guard("Python YOLO", () => CheckPython(ct)),
                Guard("YOLO weights", () => Task.FromResult(CheckWeights())),
                Guard("Face blur", () => Task.FromResult(CheckFaceBlur())),
                Guard("Model server", () => CheckModels(ct)),
                Guard("Workers", () => Task.FromResult(CheckWorkers())),
                Guard("Database", () => Task.FromResult(CheckDatabase())),
                Guard("Data folder", () => Task.FromResult(CheckFolders())),
                Guard("Notifiers", () => Task.FromResult(CheckNotifiers())),
                Guard("Access", () => Task.FromResult(users.Enabled ? AccountsCheck(Environment.GetEnvironmentVariable("CCTV_URL")) : AccessCheck(new AuthOptions().Password, Environment.GetEnvironmentVariable("CCTV_URL")))));
            var overall = checks.Any(c => c.Status == "fail") ? "fail" : checks.Any(c => c.Status == "warn") ? "warn" : "ok";
            return latest = new PreflightReport(DateTimeOffset.UtcNow, overall, checks);
        }
        finally { gate.Release(); }
    }

    private static async Task<PreflightCheck> Guard(string name, Func<Task<PreflightCheck>> run)
    {
        try { return await run(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new PreflightCheck(name, "fail", $"Check crashed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<PreflightCheck> CheckFfmpeg(CancellationToken ct)
    {
        const string name = "FFmpeg";
        var path = videos.FfmpegPath;
        try
        {
            var (code, output) = await Exec(path, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), ct);
            if (code != 0) return new PreflightCheck(name, "fail", $"{path} exited with code {code}. Set CCTV_FFMPEG to a working FFmpeg.");
            return new PreflightCheck(name, "ok", FirstLine(output));
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new PreflightCheck(name, "fail", $"Not found at {path}. Install FFmpeg and set CCTV_FFMPEG to its executable path.");
        }
        catch (TimeoutException) { return new PreflightCheck(name, "fail", $"{path} did not answer within 10 s."); }
    }

    private async Task<PreflightCheck> CheckPython(CancellationToken ct)
    {
        const string name = "Python YOLO";
        var python = PersonTracking.PythonPath(env.ContentRootPath);
        if (!File.Exists(python))
            return new PreflightCheck(name, "fail", $"Python not found at {python}. Create the YOLO environment or set CCTV_YOLO_PYTHON.");
        try
        {
            var (code, output) = await Exec(python, ["-c", "import cv2, ultralytics; print(ultralytics.__version__)"], TimeSpan.FromSeconds(90), ct);
            if (code != 0)
                return new PreflightCheck(name, "fail", "Python starts but cannot import cv2 / ultralytics: " + Cut(LastLine(output), 200));
            return new PreflightCheck(name, "ok", $"{python} (ultralytics {FirstLine(output)})");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new PreflightCheck(name, "fail", $"Cannot start {python}: {ex.Message}");
        }
        catch (TimeoutException) { return new PreflightCheck(name, "fail", "Importing ultralytics took longer than 90 s."); }
    }

    private PreflightCheck CheckWeights()
    {
        var weights = PersonTracking.WeightsPath(env.ContentRootPath);
        return File.Exists(weights)
            ? new PreflightCheck("YOLO weights", "ok", weights)
            : new PreflightCheck("YOLO weights", "fail", $"Missing {weights}. Person tracking cannot run without it.");
    }

    // Face blur always covers the head of every detected person. A face detector model adds to that, so a missing
    // one is a warning, not a failure. When blur is off there is nothing to check.
    private PreflightCheck CheckFaceBlur()
    {
        const string name = "Face blur";
        if (!store.Settings.BlurFaces) return new PreflightCheck(name, "ok", "Off in Settings. Videos and frames are stored as recorded.");
        var model = FaceBlur.ModelOverride ?? FaceBlur.BundledModel(env.ContentRootPath);
        return File.Exists(model)
            ? new PreflightCheck(name, "ok", "On: person heads plus the face detector " + Path.GetFileName(model))
            : new PreflightCheck(name, "warn", $"On, but the face detector model is missing ({model}). Only the head of each detected person is blurred.");
    }

    // One check for all configured model servers: ok when every one answers, warn when only some do, fail when none does.
    private async Task<PreflightCheck> CheckModels(CancellationToken ct)
    {
        var endpoints = ModelEndpoints.All(store.Settings);
        var results = await Task.WhenAll(endpoints.Select(e => CheckModel(e, ct)));
        if (results.Length == 1) return results[0];
        var status = results.All(r => r.Status == "ok") ? "ok" : results.Any(r => r.Status == "ok") ? "warn" : "fail";
        var detail = string.Join(" | ", results.Select((r, i) => $"{endpoints[i]}: {r.Detail}"));
        return new PreflightCheck("Model server", status, $"{results.Count(r => r.Status == "ok")} of {results.Length} servers ready. " + detail);
    }

    // Who analyses videos: this machine's workers (CCTV_WORKERS) and any remote workers that have asked for work lately.
    private PreflightCheck CheckWorkers()
    {
        const string name = "Workers";
        if (RemoteWorker.Enabled)
            return new PreflightCheck(name, "ok", $"This is a worker for {RemoteWorker.CoordinatorUrl}: {RemoteWorker.Loops} at a time.");
        var local = DescriptionWorker.LocalWorkers;
        var remote = workers.Active(TimeSpan.FromSeconds(90)).ToList();
        var accepting = WorkerApi.Enabled ? "accepting remote workers" : "remote workers off (set CCTV_WORKER_TOKEN)";
        if (local == 0 && remote.Count == 0)
            return new PreflightCheck(name, "warn", $"No worker: CCTV_WORKERS=0 here and no remote worker has connected ({accepting}). Videos will not be analysed.");
        var seen = remote.Count == 0 ? "none seen lately" : string.Join(", ", remote.Select(w => w.Name));
        return new PreflightCheck(name, "ok", $"{local} here at a time; remote: {seen} ({accepting}).");
    }

    private async Task<PreflightCheck> CheckModel(string endpoint, CancellationToken ct)
    {
        const string name = "Model server";
        var settings = store.Settings with { Endpoint = endpoint };
        const string label = "Ollama";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = clients.CreateClient("model");
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint.TrimEnd('/') + "/models");
            if (Environment.GetEnvironmentVariable("CCTV_API_KEY") is { Length: > 0 } key)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new PreflightCheck(name, "fail", $"{label} at {settings.Endpoint} answered HTTP {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var installed = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString()).OfType<string>().ToList();
            if (ModelInstalled(installed, settings.Model))
                return new PreflightCheck(name, "ok", $"{label} reachable, model '{settings.Model}' is installed.");
            var have = installed.Count == 0 ? "none installed" : string.Join(", ", installed.Take(8));
            return new PreflightCheck(name, "fail",
                $"{label} is reachable but model '{settings.Model}' is not installed (installed: {have}). Pull or create it, or change the model in Settings.");
        }
        catch (HttpRequestException)
        {
            return new PreflightCheck(name, "fail", $"Cannot reach {label} at {settings.Endpoint}." + " Start Ollama.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PreflightCheck(name, "fail", $"{label} at {settings.Endpoint} did not answer within 5 s.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new PreflightCheck(name, "warn", $"{label} answered, but the model list could not be read ({ex.GetType().Name}).");
        }
    }

    // Ollama lists "name:tag". A configured name without a tag means ":latest".
    public static bool ModelInstalled(IEnumerable<string> installed, string wanted)
    {
        wanted = wanted.Trim();
        if (wanted.Length == 0) return false;
        var candidates = wanted.Contains(':') ? new[] { wanted } : new[] { wanted, wanted + ":latest" };
        return installed.Any(id => candidates.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private PreflightCheck CheckDatabase()
    {
        const string name = "Database";
        try { store.State.Ping(); }
        catch (Exception ex) { return new PreflightCheck(name, "fail", $"{store.State.Description} cannot be used: {ex.Message}. Changes are kept in memory and lost on restart until it works again."); }
        return new PreflightCheck(name, "ok", store.State.Description + (store.State is SqlServerStateStore ? " (reachable)" : " (state kept in a file; set CCTV_SQLSERVER to use SQL Server)"));
    }

    private PreflightCheck CheckFolders()
    {
        const string name = "Data folder";
        var folders = new List<string> { store.Root };
        if (rtsp.Enabled) folders.Add(rtsp.OutputRoot);
        var notes = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".preflight-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                notes.Add($"{folder} (writable{FreeSpace(folder)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PreflightCheck(name, "fail", $"Cannot write to {folder}: {ex.Message}. Set CCTV_DATA_DIR / CCTV_RTSP_DIR to a writable folder.");
            }
        }
        return new PreflightCheck(name, "ok", string.Join("; ", notes));
    }

    private static string FreeSpace(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            return $", {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.#} GB free";
        }
        catch (Exception) { return ""; }
    }

    private static PreflightCheck AccountsCheck(string? urls) =>
        !AuthOptions.IsLoopbackOnly(urls) && (urls ?? "").Contains("http://", StringComparison.OrdinalIgnoreCase)
            ? new PreflightCheck("Access", "warn", "Per-user sign-in is on, but over plain HTTP passwords travel unencrypted. Put the app behind HTTPS (a reverse proxy).")
            : new PreflightCheck("Access", "ok", "Per-user sign-in is on (users.json).");

    internal static PreflightCheck AccessCheck(string? password, string? urls)
    {
        const string name = "Access";
        var loopback = AuthOptions.IsLoopbackOnly(urls);
        if (password is null)
            return loopback
                ? new PreflightCheck(name, "ok", "Open access, but only this computer can reach the app (loopback).")
                : new PreflightCheck(name, "warn", $"Listening on {urls} with no password: anyone on the network can watch the video and change settings. Set CCTV_UI_PASSWORD.");
        if (password.Length < 8)
            return new PreflightCheck(name, "warn", "CCTV_UI_PASSWORD is set but shorter than 8 characters.");
        if (!loopback && (urls ?? "").Contains("http://", StringComparison.OrdinalIgnoreCase))
            return new PreflightCheck(name, "warn", "Password sign-in is on, but over plain HTTP the password travels unencrypted. Put the app behind HTTPS (a reverse proxy).");
        return new PreflightCheck(name, "ok", "Password sign-in is on (CCTV_UI_PASSWORD).");
    }

    private PreflightCheck CheckNotifiers()
    {
        var active = notifiers.Where(n => n.Enabled).Select(n => n.Name).ToList();
        var real = active.Where(n => n != "log").ToList();
        if (real.Count == 0)
            return new PreflightCheck("Notifiers", "warn",
                "Only the server log is active: flagged videos are logged but nobody is notified. Configure CCTV_WEBHOOK_URL, CCTV_SMTP_* or CCTV_WA_*.");
        return new PreflightCheck("Notifiers", "ok", "Active: " + string.Join(", ", active));
    }

    private static async Task<(int Code, string Output)> Exec(string file, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        ChildProcess.ScrubSecrets(process.StartInfo);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        return (process.ExitCode, ((await stdout) + "\n" + (await stderr)).Trim());
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}