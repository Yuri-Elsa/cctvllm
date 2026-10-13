// The single way a video enters the queue from a file on disk. The manual folder refresh and
// the folder watcher both go through here, so a camera name and start time are filled in the
// same way everywhere (the notifier reads both).
public interface IVideoIngest
{
    // Validates the file and adds one job. Throws UserError for a file that cannot be used.
    // folderRoot is the linked folder: the first sub-folder under it becomes the camera name.
    Task<VideoJob> ImportAsync(string path, string? folderRoot, CancellationToken ct);

    // The "Refresh" button. Throws UserError(409) if a scan is already running.
    Task<ScanResult> ScanLinkedFolderAsync(CancellationToken ct);

    // Used by the watcher. Returns null instead of throwing when a scan is already running.
    // accept lets the caller hold back files that are not ready yet (still being written).
    Task<ScanResult?> TryScanAsync(string folder, Func<string, bool>? accept, CancellationToken ct);
}
