using System.Diagnostics;
using System.Text.Json;

// Face blur runs in tracking/face_blur.py, next to the YOLO tracker: the head of every detected person plus
// every face the face detector finds is pixelated. This class only starts that script and reports failures.
public static class FaceBlur
{
    public const string ClipName = "video-blurred.mp4";

    public enum ClipChoice { Original, Blurred, Withheld }

    // Which file a viewer gets. The blurred copy when there is one. When the job was processed with blur on but no
    // copy exists (it failed), nothing: the video is withheld rather than shown with faces. A video that has not been
    // processed yet, one with nobody in it, and a job processed with blur off are served as they are. Only an
    // administrator's explicit request (the caller checks the role) gets the original.
    public static ClipChoice Choose(VideoJob job, bool wantsOriginal) =>
        wantsOriginal ? ClipChoice.Original
        : job.FacesBlurred ? ClipChoice.Blurred
        : job.BlurFaces && !job.NoPeople && job.Status == "completed" ? ClipChoice.Withheld
        : ClipChoice.Original;

    // The copy reviewers watch. It lives in the job's own folder, so removing the job removes it too.
    public static string ClipPath(string folder) => Path.Combine(folder, ClipName);

    public static string BundledModel(string root) => Path.Combine(root, "tracking", "face_detection_yunet_2023mar.onnx");

    // CCTV_FACE_MODEL replaces the bundled YuNet model. It reaches the script through the manifest,
    // because child processes do not inherit any CCTV_* variable (see ChildProcess.ScrubSecrets).
    public static string? ModelOverride =>
        Environment.GetEnvironmentVariable("CCTV_FACE_MODEL") is { Length: > 0 } path ? path : null;

    // Pixelate faces in these image files, in place.
    public static Task Images(string root, string folder, IReadOnlyList<string> paths, CancellationToken ct) =>
        paths.Count == 0 ? Task.CompletedTask
            : Run(root, folder, new Dictionary<string, object> { ["mode"] = "images", ["paths"] = paths }, TimeSpan.FromMinutes(10), ct);

    // Write a face-blurred, browser-playable copy of the video to ClipPath(folder). Audio is not carried over.
    public static Task Clip(string root, string folder, string ffmpeg, string video, double fps, CancellationToken ct) =>
        Run(root, folder, new Dictionary<string, object>
        {
            ["mode"] = "video", ["video"] = video, ["output"] = ClipPath(folder), ["ffmpeg"] = ffmpeg, ["fps"] = fps, ["detect_fps"] = 5
        }, TimeSpan.FromMinutes(20), ct);

    private static async Task Run(string root, string folder, Dictionary<string, object> spec, TimeSpan limit, CancellationToken ct)
    {
        var python = PersonTracking.PythonPath(root);
        var weights = PersonTracking.WeightsPath(root);
        if (!File.Exists(python) || !File.Exists(weights))
            throw new UserError("Face blur needs the person-tracking dependencies, which are not installed.", 503);
        if (ModelOverride is { } model) spec["face_model"] = model;
        var manifest = Path.Combine(folder, "face-blur-input.json");
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
        foreach (var arg in new[] { Path.Combine(root, "tracking/face_blur.py"), manifest, weights })
            process.StartInfo.ArgumentList.Add(arg);
        ChildProcess.ScrubSecrets(process.StartInfo);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        // Same limits as the tracker: OpenCV opens the untrusted video with plain local files and real containers only.
        process.StartInfo.Environment["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "protocol_whitelist;file|format_whitelist;" + VideoTools.FormatWhitelist;
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            throw new UserError("Face blur timed out. Try a shorter video.", 504);
        }
        await output;
        if (process.ExitCode != 0)
        {
            var detail = await error;
            throw new UserError("Face blur failed: " + detail[^Math.Min(detail.Length, 350)..], 502);
        }
    }
}
