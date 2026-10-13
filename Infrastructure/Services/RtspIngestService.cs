using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

// Cuts each configured RTSP stream into short mp4 segments, one sub-folder per camera.
// It does not touch the queue: FolderWatcherService watches OutputRoot and imports finished
// segments, and the camera name comes from the sub-folder, exactly as for a linked folder.
public sealed partial class RtspIngestService(QueueStore store, VideoTools videos, IOptions<RtspOptions> options,
    ILogger<RtspIngestService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CameraStatus> status = new();
    private readonly Lazy<IReadOnlyList<RtspCamera>> cameras =
        new(() => options.Value.LoadCameras(message => logger.LogWarning("{Message}", message)));

    public bool Enabled => cameras.Value.Count > 0;
    public string OutputRoot => options.Value.OutputDir ?? Path.Combine(store.Root, "rtsp");
    public IReadOnlyList<CameraStatus> Snapshot() => status.Values.OrderBy(s => s.Camera, StringComparer.OrdinalIgnoreCase).ToList();

    // True while ffmpeg may still be writing this file: it is the newest segment of a camera whose
    // ffmpeg is running (names sort by time). The watcher must not import it yet. This is exact on
    // every OS, unlike judging by file size, which can stay the same between ffmpeg's buffered writes.
    public bool IsBeingWritten(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(OutputRoot), StringComparison.OrdinalIgnoreCase)) return false;
            if (!status.TryGetValue(Path.GetFileName(dir), out var camera) || camera.State == "retrying") return false;
            var newest = Directory.EnumerateFiles(dir, "*.mp4").Select(Path.GetFileName)
                .OrderByDescending(name => name, StringComparer.Ordinal).FirstOrDefault();
            return string.Equals(newest, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("RTSP ingest is off. Set CCTV_CAMERAS and CCTV_RTSP_<NAME> to turn it on.");
            return;
        }
        Directory.CreateDirectory(OutputRoot);
        logger.LogInformation("RTSP ingest on: {Count} camera(s), {Seconds}s segments, output {Dir}.",
            cameras.Value.Count, options.Value.SegmentSeconds, OutputRoot);
        await Task.WhenAll(cameras.Value.Select(camera => RunCamera(camera, stoppingToken)));
    }

    private async Task RunCamera(RtspCamera camera, CancellationToken ct)
    {
        var dir = Path.Combine(OutputRoot, camera.Name);
        Directory.CreateDirectory(dir);
        Update(camera.Name, s => s);   // appears in /api/cameras as "starting"
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            string? reason;
            try { reason = await RunOnce(camera, dir, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { reason = ex is UserError ? ex.Message : ex.GetType().Name; }

            logger.LogWarning("Camera {Camera} stopped: {Reason}. Retrying in {Seconds}s.", camera.Name, reason, delay.TotalSeconds);
            Update(camera.Name, s => s with { State = "retrying", Restarts = s.Restarts + 1, LastError = reason });
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
            // A stream that stayed up for a while starts over at a short delay; a failing one backs off to 60 s.
            delay = DateTime.UtcNow - started > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    // Runs ffmpeg until it exits or stalls. Returns why it stopped.
    private async Task<string?> RunOnce(RtspCamera camera, string dir, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        long lastProgress = started.Ticks;
        var errors = new ConcurrentQueue<string>();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(videos.FfmpegPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        foreach (var arg in Args(camera, dir)) process.StartInfo.ArgumentList.Add(arg);
        // Progress lines on stdout are how we know data is flowing. File times are not used for this:
        // on Windows they can lag while another process still has the file open.
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Interlocked.Exchange(ref lastProgress, DateTime.UtcNow.Ticks);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errors.Enqueue(Redact(e.Data, camera.Url));
            while (errors.Count > 3) errors.TryDequeue(out var dropped);
        };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        { throw new UserError("FFmpeg is unavailable. Set CCTV_FFMPEG to its executable path.", 503); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = process.WaitForExitAsync(CancellationToken.None);

        var stalled = false;
        try
        {
            while (true)
            {
                var finished = await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2), ct));
                ct.ThrowIfCancellationRequested();
                if (finished == exited) break;
                var last = new DateTime(Interlocked.Read(ref lastProgress), DateTimeKind.Utc);
                if (last > started) Update(camera.Name, s => s with { State = "running", LastDataAt = new DateTimeOffset(last), LastError = null });
                if (DateTime.UtcNow - (last > started ? last : started) > options.Value.StallAfter) { stalled = true; break; }
            }
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            await exited;
        }
        if (stalled) return $"no data for {options.Value.StallAfter.TotalSeconds:0} s";
        var detail = string.Join(" | ", errors);
        return $"ffmpeg exited with code {process.ExitCode}" + (detail.Length > 0 ? $": {detail}" : "");
    }

    // -c:v copy: no re-encoding, so the CPU stays free for the model. -an: camera audio such as
    // G.711 often cannot go into mp4, and the system does not use it.
    private string[] Args(RtspCamera camera, string dir) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats",
        "-progress", "pipe:1", "-stats_period", "5",
        "-rtsp_transport", "tcp", "-i", camera.Url,
        "-an", "-c:v", "copy",
        "-f", "segment", "-segment_time", options.Value.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
        "-reset_timestamps", "1", "-strftime", "1",
        Path.Combine(dir, "%Y%m%d_%H%M%S.mp4")
    ];

    private void Update(string camera, Func<CameraStatus, CameraStatus> change) =>
        status.AddOrUpdate(camera,
            _ => change(new CameraStatus(camera, "starting", null, 0, null)),
            (_, current) => change(current));

    // ffmpeg may echo the URL (with its password) in an error message. Remove it before it is stored or logged.
    private static string Redact(string text, string url) => Credentials().Replace(text.Replace(url, "<rtsp-url>"), "***@");

    [GeneratedRegex(@"(?<=://)[^/@\s]+@")]
    private static partial Regex Credentials();
}
