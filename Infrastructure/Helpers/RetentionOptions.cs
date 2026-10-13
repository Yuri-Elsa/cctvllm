using System.Globalization;

// Read from environment variables. All of this applies only to RTSP segments (files inside the RTSP
// folder). A video from a linked folder or an upload is never deleted and never dropped.
public sealed class RetentionOptions
{
    // Videos waiting per camera. Over the limit, the oldest waiting ones are dropped. 0 = no limit.
    public int MaxQueuePerCamera => (int)Math.Max(0, Number("CCTV_MAX_QUEUE_PER_CAMERA", 20));

    // Segments where nobody was detected: file and record are removed this long after import.
    public TimeSpan KeepEmpty => TimeSpan.FromMinutes(Math.Max(0, Number("CCTV_KEEP_EMPTY_MINUTES", 10)));

    // Segments with people that were not flagged, and failed or dropped ones.
    public TimeSpan KeepUnflagged => TimeSpan.FromHours(Math.Max(0, Number("CCTV_KEEP_HOURS", 24)));

    // Flagged segments (suspicious or weapon). Never removed to free disk space, only by age.
    public TimeSpan KeepFlagged => TimeSpan.FromDays(Math.Max(0, Number("CCTV_KEEP_FLAGGED_DAYS", 30)));

    // Size limit for the RTSP folder. 0 = no limit.
    public long MaxDiskBytes => (long)(Math.Max(0, Number("CCTV_MAX_DISK_GB", 20)) * 1024 * 1024 * 1024);

    // A file in the RTSP folder that no video record points to, and that is this old, is deleted.
    public TimeSpan OrphanAge => TimeSpan.FromMinutes(Math.Max(0.1, Number("CCTV_KEEP_ORPHAN_MINUTES", 60)));

    public TimeSpan SweepEvery => TimeSpan.FromSeconds(Math.Max(5, Number("CCTV_RETENTION_INTERVAL", 30)));

    private static double Number(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
