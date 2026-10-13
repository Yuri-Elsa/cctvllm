using Microsoft.Extensions.Options;

// Polls the folders that feed the queue and imports new videos once they have finished being
// written: the folder linked in the UI (CCTV_WATCH) and the RTSP segment folder (CCTV_CAMERAS).
// Polling is used instead of FileSystemWatcher because that is unreliable on network shares.
public sealed class FolderWatcherService(QueueStore store, IVideoIngest ingest, RtspIngestService rtsp, ScanCoordinator scans,
    IOptions<WatchOptions> options, ILogger<FolderWatcherService> logger) : BackgroundService
{
    private readonly WatchOptions watch = options.Value;

    // Size and write time seen on the previous poll. A file is "ready" when both are unchanged.
    private readonly Dictionary<string, (long Size, DateTime Write)> seen = new();

    // Files this watcher already tried, keyed by path + size + write time. Keeps a removed job
    // from coming back on the next poll, and a file that cannot be read from being retried
    // (and logged) every few seconds. A changed file gets a new key, so it is tried again.
    // Lost on restart.
    private readonly HashSet<string> handled = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No early return when both switches are off: the loop must still run, because linking a folder
        // or pressing Refresh hands the import to this service (see ScanCoordinator).
        if (!watch.Enabled && !rtsp.Enabled)
            logger.LogInformation("Automatic folder watch is off (CCTV_WATCH / CCTV_CAMERAS). A linked folder is still imported when you link it or press Refresh.");
        else
            logger.LogInformation("Folder watch on: every {Seconds}s, linked folder {Linked}, RTSP {Rtsp}, auto-run {AutoRun}.",
                watch.PollInterval.TotalSeconds, watch.Enabled, rtsp.Enabled, watch.AutoRun);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Poll(stoppingToken);
                await scans.WaitAsync(watch.PollInterval, stoppingToken);   // wakes early on a link / refresh request
            }
        }
        catch (OperationCanceledException) { }
    }

    // The root decides the camera name: the first sub-folder under it.
    private IEnumerable<string> Roots()
    {
        if (watch.Enabled && store.LinkedFolder is { } linked && Directory.Exists(linked)) yield return linked;
        if (rtsp.Enabled && Directory.Exists(rtsp.OutputRoot)) yield return rtsp.OutputRoot;
    }

    private async Task Poll(CancellationToken ct)
    {
        try
        {
            // A link / Refresh request: scan the linked folder now, without the "file is stable" wait.
            var requested = scans.TryTake() ? store.LinkedFolder : null;
            if (requested is not null) await ScanRequested(requested, ct);

            foreach (var root in Roots())
            {
                if (root == requested) continue;   // just scanned
                var result = await ingest.TryScanAsync(root, IsReady, ct);
                if (result is null) continue;   // another scan is running; try again next tick
                foreach (var error in result.Errors) logger.LogWarning("Watch import failed: {Error}", error);
                if (result.Added > 0)
                {
                    logger.LogInformation("Watch added {Count} video(s) from {Root}.", result.Added, root);
                    if (watch.AutoRun) store.SetRunning(true);
                }
            }
            foreach (var path in seen.Keys.Where(p => !File.Exists(p)).ToList()) seen.Remove(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Folder watch poll failed."); }
    }

    private async Task ScanRequested(string folder, CancellationToken ct)
    {
        scans.Begin();
        ScanResult? result = null;
        try
        {
            result = await ingest.TryScanAsync(folder, null, ct);
            if (result is null)
            {   // the gate is held by another scan: ask again shortly instead of spinning
                await Task.Delay(500, ct);
                scans.Request();
                return;
            }
            foreach (var error in result.Errors) logger.LogWarning("Linked folder import failed: {Error}", error);
            logger.LogInformation("Linked folder scan: {Added} added, {Skipped} already imported, {Errors} skipped.",
                result.Added, result.Skipped, result.Errors.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Linked folder scan failed.");
            result = new ScanResult(folder, 0, 0, ["The folder could not be read. Check the server log."]);
        }
        finally { scans.Finish(result); }
    }

    // Called only for files that are not imported yet. True means: import it now.
    private bool IsReady(string path)
    {
        if (rtsp.IsBeingWritten(path)) return false;   // ffmpeg still has this segment open
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return false; }
        catch (IOException) { return false; }

        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (handled.Contains(key)) return false;

        var current = (info.Length, info.LastWriteTimeUtc);
        var unchanged = seen.TryGetValue(path, out var previous) && previous == current && info.Length > 0;
        seen[path] = current;
        if (!unchanged || IsLocked(path)) return false;

        handled.Add(key);
        seen.Remove(path);
        return true;
    }

    // On Windows a file still being written or copied cannot be opened exclusively.
    private static bool IsLocked(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}