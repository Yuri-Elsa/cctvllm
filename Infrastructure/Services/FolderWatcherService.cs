using Microsoft.Extensions.Options;

// Polls the linked folder and imports new videos once they have finished being written.
// Polling is used instead of FileSystemWatcher because that is unreliable on network shares.
public sealed class FolderWatcherService(QueueStore store, IVideoIngest ingest, IOptions<WatchOptions> options,
    ILogger<FolderWatcherService> logger) : BackgroundService
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
        if (!watch.Enabled)
        {
            logger.LogInformation("Folder watch is off. Set CCTV_WATCH=1 to turn it on.");
            return;
        }
        logger.LogInformation("Folder watch on: every {Seconds}s, auto-run {AutoRun}.", watch.PollInterval.TotalSeconds, watch.AutoRun);
        using var timer = new PeriodicTimer(watch.PollInterval);
        try
        {
            do { await Poll(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private async Task Poll(CancellationToken ct)
    {
        var folder = store.LinkedFolder;
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            var result = await ingest.TryScanAsync(folder, IsReady, ct);
            if (result is null) return;   // a manual refresh is running; try again next tick
            foreach (var error in result.Errors) logger.LogWarning("Watch import failed: {Error}", error);
            if (result.Added > 0)
            {
                logger.LogInformation("Watch added {Count} video(s).", result.Added);
                if (watch.AutoRun) store.SetRunning(true);
            }
            foreach (var path in seen.Keys.Where(p => !File.Exists(p)).ToList()) seen.Remove(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Folder watch poll failed."); }
    }

    // Called only for files that are not imported yet. True means: import it now.
    private bool IsReady(string path)
    {
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

    // On Windows a file still being copied cannot be opened exclusively.
    private static bool IsLocked(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
