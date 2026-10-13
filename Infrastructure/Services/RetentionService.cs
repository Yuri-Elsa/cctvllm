using Microsoft.Extensions.Options;

// Keeps continuous recording from filling the queue and the disk. It sweeps every few seconds:
//   1. drops the oldest waiting videos of a camera once it has more than the limit
//   2. removes segments past their age (empty, unflagged, flagged have separate limits)
//   3. removes stray files that no video record points to
//   4. removes the oldest unflagged segments while the folder is over its size limit
// Only files inside the RTSP folder are ever deleted, and never a flagged one for disk space.
public sealed class RetentionService(QueueStore store, RtspIngestService rtsp, IOptions<RetentionOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    private readonly RetentionOptions keep = options.Value;
    private bool warnedDisk, warnedOverlap;

    private sealed class Report
    {
        public int Dropped, Empty, Unflagged, Flagged, Disk, Orphans;
        public bool Any => Dropped + Empty + Unflagged + Flagged + Disk + Orphans > 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Retention: {Queue} waiting per camera, empty {Empty} min, unflagged {Hours} h, flagged {Days} d, folder limit {Limit}.",
            keep.MaxQueuePerCamera, keep.KeepEmpty.TotalMinutes, keep.KeepUnflagged.TotalHours, keep.KeepFlagged.TotalDays,
            keep.MaxDiskBytes > 0 ? Pretty(keep.MaxDiskBytes) : "none");
        using var timer = new PeriodicTimer(keep.SweepEvery);
        try
        {
            do { Sweep(); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private void Sweep()
    {
        try
        {
            var report = new Report();
            DropBacklog(report);
            if (CanDeleteFiles())
            {
                RemoveExpired(report);
                RemoveStrays(report);
                EnforceDiskLimit(report);
            }
            if (report.Any)
                logger.LogInformation("Retention: dropped {Dropped} waiting, removed {Empty} empty, {Unflagged} unflagged, {Flagged} flagged, {Disk} for disk space, {Orphans} stray file(s).",
                    report.Dropped, report.Empty, report.Unflagged, report.Flagged, report.Disk, report.Orphans);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Retention sweep failed."); }
    }

    // Segments recorded by this service. Retention touches nothing else.
    private List<VideoJob> Segments() =>
        store.Jobs().Where(j => j.SourcePath is not null && rtsp.OwnsFile(j.SourcePath)).ToList();

    private static bool Finished(VideoJob job) => job.Status is "completed" or "failed" or "cancelled";
    private static bool IsFlagged(VideoJob job) => new[] { job }.Flagged().Any();

    // Processing is slower than real time, so waiting videos pile up. Keep the newest ones.
    // A dropped video keeps its file until the normal age limit, so it can still be retried.
    private void DropBacklog(Report report)
    {
        var limit = keep.MaxQueuePerCamera;
        if (limit <= 0) return;
        foreach (var camera in Segments().Where(j => j.Status == "queued").GroupBy(j => j.CameraName ?? ""))
        {
            foreach (var job in camera.OrderBy(j => j.SegmentStart ?? j.Created).SkipLast(limit))
            {
                var name = string.IsNullOrEmpty(camera.Key) ? "this camera" : camera.Key;
                // The check runs inside the update, under the store's lock, so a video the worker has
                // just taken is left alone.
                store.Update(job.Id, j => j.Status != "queued" ? j : j with
                {
                    Status = "cancelled", Stage = "Dropped (backlog)",
                    Error = $"Dropped: more than {limit} videos were waiting for {name}."
                });
                if (store.Get(job.Id).Stage == "Dropped (backlog)") report.Dropped++;
            }
        }
    }

    private void RemoveExpired(Report report)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in Segments().Where(Finished))
        {
            var flagged = IsFlagged(job);
            var limit = flagged ? keep.KeepFlagged : job.NoPeople ? keep.KeepEmpty : keep.KeepUnflagged;
            if (now - job.Created < limit || !Delete(job)) continue;
            if (flagged) report.Flagged++; else if (job.NoPeople) report.Empty++; else report.Unflagged++;
        }
    }

    // A file nobody points to: a segment cut off when ffmpeg was stopped, or one whose video was
    // removed in the UI. Waiting this long means a finished segment still has time to be imported.
    private void RemoveStrays(Report report)
    {
        var root = rtsp.OutputRoot;
        if (!Directory.Exists(root)) return;
        var known = new HashSet<string>(store.Jobs().Select(j => j.SourcePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!rtsp.OwnsFile(file) || known.Contains(file) || rtsp.IsBeingWritten(file)) continue;
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < keep.OrphanAge) continue;
                File.Delete(file);
                report.Orphans++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void EnforceDiskLimit(Report report)
    {
        var limit = keep.MaxDiskBytes;
        var root = rtsp.OutputRoot;
        if (limit <= 0 || !Directory.Exists(root)) return;
        var size = FolderSize(root);
        if (size > limit)
        {
            foreach (var job in Segments().Where(j => Finished(j) && !IsFlagged(j)).OrderBy(j => j.Created))
            {
                if (size <= limit) break;
                var length = LengthOf(job.SourcePath!);
                if (Delete(job)) { size -= length; report.Disk++; }
            }
        }
        if (size > limit && !warnedDisk)
            logger.LogWarning("The RTSP segments take {Size}, over the {Limit} limit, and nothing more can be removed: flagged and waiting videos are kept.",
                Pretty(size), Pretty(limit));
        warnedDisk = size > limit;
    }

    // File first, then the record. If the file cannot be deleted (for example it is being played),
    // the record stays and the next sweep tries again.
    private bool Delete(VideoJob job)
    {
        try { if (File.Exists(job.SourcePath!)) File.Delete(job.SourcePath!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        try { store.Remove(job.Id); }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException) { }
        return store.Jobs().All(j => j.Id != job.Id);
    }

    // The RTSP folder is meant to be ours alone. If it overlaps the folder linked in the UI, deleting
    // files could remove the user's own videos, so no file is deleted at all.
    private bool CanDeleteFiles()
    {
        var linked = store.LinkedFolder;
        if (linked is null) return true;
        var root = WithSeparator(rtsp.OutputRoot);
        var other = WithSeparator(linked);
        var overlap = root.StartsWith(other, StringComparison.OrdinalIgnoreCase) || other.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        if (overlap && !warnedOverlap)
            logger.LogError("Retention will not delete files: the RTSP folder and the linked folder overlap. Use CCTV_RTSP_DIR for a folder used only by RTSP.");
        warnedOverlap = overlap;
        return !overlap;
    }

    private static string WithSeparator(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static long LengthOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    // Only segment files count: those are the ones this service may delete.
    private long FolderSize(string root)
    {
        long total = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { if (rtsp.OwnsFile(file.FullName)) total += file.Length; }
            catch (IOException) { }
        }
        return total;
    }

    private static string Pretty(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1024d / 1024 / 1024:0.##} GB" : $"{bytes / 1024d / 1024:0.##} MB";
}
