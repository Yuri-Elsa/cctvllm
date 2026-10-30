using System.Diagnostics;
using System.Text.Json;

public sealed record TrackedFrame(string Path, string PlainPath, double Timestamp, JsonElement People, double Score, bool Uncertain);

public sealed record TrackingOutput(TrackedFrame[] Frames, string Motion, PersonSample[] Samples);

public sealed class PersonTracking
{
    // Detection runs denser than the frames actually sent to the vision model: motion is
    // only measurable with closely spaced samples, while each frame forwarded to the model
    // costs roughly a thousand vision tokens.
    public const double SampleInterval = 0.5;   // seconds between detections (2 fps)
    public const double ModelMinGap = 1.0;      // minimum seconds between frames sent onward
    public const double MotionConf = 0.25;

    public static string PythonPath(string root) => Environment.GetEnvironmentVariable("CCTV_YOLO_PYTHON") ??
        Path.GetFullPath(Path.Combine(root, "../../work/yolo-env/bin/python"));
    public static string WeightsPath(string root) => Path.Combine(root, "tracking/yolov8n.pt");

    public static async Task<Detection> Run(
        string root, string folder, string videoPath, double fps, double conf, double modelMinGap, CancellationToken ct)
    {
        var tracked = await Track(root, folder, videoPath, fps, conf, modelMinGap, null, false, ct);
        return Shape(tracked.Frames, tracked.Motion, tracked.Samples);
    }

    public static Detection Shape(TrackedFrame[] frames, string motion, PersonSample[] samples) =>
        new(frames.Select(f => f.Path).ToArray(), frames.Select(f => f.PlainPath).ToArray(), frames.Select(f => f.Timestamp).ToArray(),
            JsonSerializer.Serialize(frames.Select(f => new { timestamp = f.Timestamp, people = f.People })), motion, samples);

    public static async Task<TrackingOutput> Track(
        string root, string folder, string videoPath, double fps, double conf, double modelMinGap,
        double? verifyLow, bool motionOnly, CancellationToken ct)
    {
        var python = PythonPath(root);
        var weights = WeightsPath(root);
        if (!File.Exists(python) || !File.Exists(weights))
            throw new UserError("Person tracking is not installed. Install the tracking dependencies to filter frames containing people.", 503);
        if (!File.Exists(videoPath))
            throw new UserError("This video is no longer at its original path in the linked folder.", 404);
        var manifest = Path.Combine(folder, motionOnly ? "tracking-motion-input.json" : "tracking-input.json");
        var spec = new Dictionary<string, object>
        {
            // May sit outside the job folder: a linked video is read where the user keeps it.
            ["video"] = videoPath,
            ["fps"] = fps,
            ["max_frames"] = VideoTools.MaxFrames,
            ["sample_interval"] = SampleInterval,
            ["model_min_gap"] = modelMinGap,
            ["conf"] = conf
        };
        if (verifyLow is { } low) spec["verify_low"] = low;
        if (motionOnly) spec["motion_only"] = true;
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(spec), ct);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = root
            }
        };
        foreach (var arg in new[] { Path.Combine(root, "tracking/track_people.py"), manifest, weights })
            process.StartInfo.ArgumentList.Add(arg);
        ChildProcess.ScrubSecrets(process.StartInfo);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        // OpenCV opens the same untrusted file with its own bundled ffmpeg. Give it the same limits as
        // VideoTools.SafeInput: plain local files and real video containers, no playlists or URLs.
        process.StartInfo.Environment["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "protocol_whitelist;file|format_whitelist;" + VideoTools.FormatWhitelist;
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            throw new UserError("Person tracking timed out. Try a shorter video.", 504);
        }
        await output;
        if (process.ExitCode != 0)
        {
            var detail = await error;
            throw new UserError("Person tracking failed: " + detail[^Math.Min(detail.Length, 350)..], 502);
        }
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "tracking-result.json"), ct));
        // The script returns {frames, motion}: the frames selected for the vision model, plus
        // a motion timeline covering every detection sample (denser than those frames). A bare
        // array is still accepted so a result file from an older run stays readable.
        var resultRoot = doc.RootElement;
        var framesElement = resultRoot.ValueKind == JsonValueKind.Object ? resultRoot.GetProperty("frames") : resultRoot;
        var motion = resultRoot.ValueKind == JsonValueKind.Object && resultRoot.TryGetProperty("motion", out var timeline)
            ? timeline.GetRawText() : "[]";
        var samples = resultRoot.ValueKind == JsonValueKind.Object && resultRoot.TryGetProperty("samples", out var sampleList) && sampleList.ValueKind == JsonValueKind.Array
            ? sampleList.EnumerateArray().Select(s => new PersonSample(s.GetProperty("timestamp").GetDouble(), s.GetProperty("score").GetDouble())).ToArray()
            : Array.Empty<PersonSample>();
        var records = framesElement.EnumerateArray().ToArray();
        if (records.Length > VideoTools.MaxFrames) throw new UserError("Person detection exceeded the frame cap.", 502);
        records = records.Where(r => r.GetProperty("people").GetArrayLength() > 0).ToArray();
        var frames = records.Select(r => new TrackedFrame(
            r.GetProperty("path").GetString()!, r.GetProperty("plain_path").GetString()!, r.GetProperty("timestamp").GetDouble(),
            r.GetProperty("people").Clone(),
            r.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0,
            r.TryGetProperty("uncertain", out var flag) && flag.ValueKind == JsonValueKind.True)).ToArray();
        return new TrackingOutput(frames, motion, samples);
    }
}