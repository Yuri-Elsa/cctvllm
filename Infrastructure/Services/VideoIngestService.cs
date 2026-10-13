public sealed class VideoIngestService(QueueStore store, VideoTools videos) : IVideoIngest
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".avi", ".mkv", ".mpeg", ".mpg", ".mts", ".m2ts", ".wmv", ".3gp", ".ogv"
    };

    // One scan at a time, whether it comes from the Refresh button or from the watcher.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<VideoJob> ImportAsync(string path, string? folderRoot, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new UserError("File not found.", 404);
        if (file.Length > VideoTools.MaxBytes) throw new UserError("Maximum file size is 500 MB.");
        var media = await videos.Inspect(path, ct);
        var job = new VideoJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileName(path),
            SourcePath = path,
            Duration = media.Duration,
            Fps = media.Fps,
            Bytes = file.Length,
            CameraName = folderRoot is null ? null : CameraNameFor(folderRoot, path),
            SegmentStart = StartTimeFor(file)
        };
        var dir = store.Folder(job.Id); Directory.CreateDirectory(dir);
        try { await videos.Poster(path, Path.Combine(dir, "poster.jpg"), ct); store.Add(job); return job; }
        catch { Directory.Delete(dir, true); throw; }
    }

    public async Task<ScanResult> ScanLinkedFolderAsync(CancellationToken ct)
    {
        var folder = store.LinkedFolder ?? throw new UserError("Link a folder first.");
        if (!Directory.Exists(folder)) throw new UserError("The linked folder is no longer there. Link it again.", 404);
        if (!await gate.WaitAsync(0, ct)) throw new UserError("A refresh is already running.", 409);
        try { return await Scan(folder, null, ct); }
        finally { gate.Release(); }
    }

    public async Task<ScanResult?> TryScanAsync(string folder, Func<string, bool>? accept, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return null;
        try { return await Scan(folder, accept, ct); }
        finally { gate.Release(); }
    }

    private async Task<ScanResult> Scan(string folder, Func<string, bool>? accept, CancellationToken ct)
    {
        var errors = new List<string>();
        int added = 0, skipped = 0;
        foreach (var path in EnumerateVideos(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (store.AlreadyImported(path)) { skipped++; continue; }
            if (accept is not null && !accept(path)) continue;
            try { await ImportAsync(path, folder, ct); added++; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetRelativePath(folder, path)}: {(ex is UserError ? ex.Message : "could not be read.")}");
            }
        }
        return new ScanResult(folder, added, skipped, errors);
    }

    private static IEnumerable<string> EnumerateVideos(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(p => VideoExtensions.Contains(Path.GetExtension(p)))
            .Where(p => !Path.GetFileName(p).StartsWith('.'))   // .DS_Store, macOS ._ resource forks
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    // <linked folder>/<camera>/clip.mp4  ->  "<camera>". A file directly in the linked folder has no camera.
    public static string? CameraNameFor(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 ? parts[0] : null;
    }

    // Best effort. A recorder sets creation time when the clip starts and write time when it ends;
    // a copied file keeps its old write time but gets a new creation time. The earlier of the two
    // is right for both. Reading a time out of the file name is a possible later refinement.
    private static DateTimeOffset StartTimeFor(FileInfo file) =>
        new(file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc, TimeSpan.Zero);
}
