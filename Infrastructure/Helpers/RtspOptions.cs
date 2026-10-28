using System.Text.RegularExpressions;

public sealed record RtspCamera(string Name, string Url);

// Read from environment variables, like WatchOptions and NotificationOptions.
//   CCTV_CAMERAS=lobby,gudang          camera names (letters, digits, - and _)
//   CCTV_RTSP_LOBBY=rtsp://user:pw@host/stream     one URL per camera, name upper-cased, - becomes _
// The URL may hold a password, so it only ever comes from the environment: it is never saved
// in state.json, never logged, and never returned by the API.
public sealed partial class RtspOptions
{
    public int SegmentSeconds => Math.Clamp(Int("CCTV_RTSP_SEGMENT_SECONDS", 60), 5, 300);

    // ffmpeg is restarted when it reports no progress for this long.
    public TimeSpan StallAfter => TimeSpan.FromSeconds(Math.Max(10, Int("CCTV_RTSP_STALL_SECONDS", Math.Max(45, SegmentSeconds * 3))));

    // Live mode (CCTV_LIVE=1). Instead of cutting the stream into fixed segments, a watcher process looks at the
    // stream all the time and saves a clip only while a person is in view (see tracking/live_watch.py).
    public bool Live => Environment.GetEnvironmentVariable("CCTV_LIVE") is { } value &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    public int LivePreRoll => Math.Clamp(Int("CCTV_LIVE_PREROLL", 5), 0, 30);       // seconds kept from before the first detection
    public int LivePostRoll => Math.Clamp(Int("CCTV_LIVE_POSTROLL", 4), 1, 60);     // seconds without a person before a clip ends
    public int LiveMaxClip => Math.Clamp(Int("CCTV_LIVE_MAX_CLIP", 20), 5, 300);    // a clip is closed after this long, then the next begins
    public int LiveFps => Math.Clamp(Int("CCTV_LIVE_FPS", 6), 2, 15);               // frames per second in the clips
    public int LiveSize => Math.Clamp(Int("CCTV_LIVE_SIZE", 960), 320, 1920);       // longest side of the saved picture
    public int LiveConfirm => Math.Clamp(Int("CCTV_LIVE_CONFIRM", 2), 1, 10);       // detections in a row that confirm a person
    // The watcher reports every ~2 s while pictures arrive; it is restarted after this long without a word.
    public TimeSpan LiveStallAfter => TimeSpan.FromSeconds(Math.Max(10, Int("CCTV_RTSP_STALL_SECONDS", 30)));

    // Where segments are written: <dir>/<camera>/<yyyyMMdd_HHmmss>.mp4. Default: <data dir>/rtsp.
    public string? OutputDir => Environment.GetEnvironmentVariable("CCTV_RTSP_DIR") is { Length: > 0 } dir ? dir : null;

    public IReadOnlyList<RtspCamera> LoadCameras(Action<string> warn)
    {
        var cameras = new List<RtspCamera>();
        var names = (Environment.GetEnvironmentVariable("CCTV_CAMERAS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!ValidName().IsMatch(name)) { warn($"Camera name '{name}' is skipped: use letters, digits, - and _ (max 40)."); continue; }
            var key = "CCTV_RTSP_" + name.ToUpperInvariant().Replace('-', '_');
            var url = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(url)) { warn($"Camera '{name}' is skipped: {key} is not set."); continue; }
            if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
            { warn($"Camera '{name}' is skipped: {key} must start with rtsp://."); continue; }
            cameras.Add(new RtspCamera(name, url.Trim()));
        }
        return cameras;
    }

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ValidName();
}
