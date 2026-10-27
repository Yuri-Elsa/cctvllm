using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public sealed class VideoTools(IWebHostEnvironment env)
{
    public static readonly int MaxFrames = int.TryParse(Environment.GetEnvironmentVariable("CCTV_MAX_FRAMES"), out var frames) && frames >= 2 ? frames : 150;
    public const long MaxBytes = 500L * 1024 * 1024;
    // Total size of uploaded (not linked) videos. Retention never removes these, so they need a cap.
    public static readonly long MaxUploadBytes = double.TryParse(Environment.GetEnvironmentVariable("CCTV_MAX_UPLOAD_GB"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var uploadGb) && uploadGb > 0
        ? (long)(uploadGb * 1024 * 1024 * 1024) : 20L * 1024 * 1024 * 1024;
    private readonly string ffmpeg = Environment.GetEnvironmentVariable("CCTV_FFMPEG") is { Length: > 0 } configured ? configured : DevFfmpeg(env.ContentRootPath);

    // The author's own build sits next to the project. Anywhere else, fall back to ffmpeg on the PATH.
    private static string DevFfmpeg(string root)
    {
        var dev = Path.GetFullPath(Path.Combine(root, "../../work/demo-deps/imageio_ffmpeg/binaries/ffmpeg-macos-aarch64-v7.1"));
        return File.Exists(dev) ? dev : "ffmpeg";
    }
    public string FfmpegPath => ffmpeg;

    // Uploaded and linked files are untrusted input. Without these, ffmpeg will follow playlists
    // (HLS, concat) to other files or URLs. Allow only plain local files and real video containers.
    internal const string FormatWhitelist = "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,mpegts,mpeg,mpegvideo,asf,ogg,flv,h264,hevc,m4v";

    private static readonly string[] SafeInput =
    [
        "-protocol_whitelist", "file",
        "-format_whitelist", FormatWhitelist
    ];

    private async Task<(int Code, string Error)> Run(string[] args, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        ChildProcess.ScrubSecrets(process.StartInfo);
        try { process.Start(); }
        catch (Exception) { throw new UserError("FFmpeg is unavailable. Set CCTV_FFMPEG to its executable path.", 503); }
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    public async Task<(double Duration, double Fps)> Inspect(string path, CancellationToken ct)
    {
        var result = await Run(["-hide_banner", .. SafeInput, "-i", path], ct);
        var durationMatch = Regex.Match(result.Error, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        var videoLine = result.Error.Split('\n').FirstOrDefault(x => x.Contains("Video:"));
        if (!durationMatch.Success || videoLine is null) throw new UserError("This file is not a readable video.", 422);
        double Part(int n) => double.Parse(durationMatch.Groups[n].Value, CultureInfo.InvariantCulture);
        var duration = Part(1) * 3600 + Part(2) * 60 + Part(3);
        if (duration <= 0 || duration > 300) throw new UserError(duration > 300 ? "Video exceeds the 5-minute limit." : "Video duration is invalid.", 422);
        var fpsMatch = Regex.Match(videoLine, @"([\d.]+) fps");
        var fps = fpsMatch.Success ? double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 25;
        if (fps <= 0 || fps > 240) fps = 25;
        return (duration, fps);
    }

    public async Task Poster(string input, string output, CancellationToken ct)
    {
        var r = await Run(["-y", .. SafeInput, "-i", input, "-frames:v", "1", "-vf", "scale=320:-2", "-pix_fmt", "yuvj420p", output], ct);
        if (r.Code != 0 || !File.Exists(output)) throw new UserError("Could not decode this video.", 422);
    }

    public async Task<(string[] Paths, double[] Timestamps)> SampleFrames(string videoPath, string folder, double interval, CancellationToken ct)
    {
        var output = Path.Combine(folder, "llm-frames");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);
        var rate = (1 / interval).ToString("0.######", CultureInfo.InvariantCulture);
        var filter = $"fps={rate},scale=w='if(gte(iw,ih),min(640,iw),trunc(iw*min(1,640/ih)))':h='if(gte(iw,ih),trunc(ih*min(1,640/iw)),min(640,ih))':flags=area";
        var r = await Run(["-y", .. SafeInput, "-i", videoPath, "-vf", filter, "-frames:v", MaxFrames.ToString(CultureInfo.InvariantCulture), Path.Combine(output, "%04d.png")], ct);
        var all = Directory.GetFiles(output, "*.png").Order().ToArray();
        if (r.Code != 0 || all.Length == 0) throw new UserError("Could not extract video frames.", 422);
        return (all, Enumerable.Range(0, all.Length).Select(i => Math.Round(i * interval, 2)).ToArray());
    }

    public async Task<(string[] Paths, double[] Timestamps)> Frames(string folder, double fps, CancellationToken ct)
    {
        var output = Path.Combine(folder, "frames");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);
        // Matches the notebook's frame-index stride and full-duration even subsampling.
        var stride = Math.Max(1, (int)Math.Round(fps * 2, MidpointRounding.ToEven));
        var filter = $"select=not(mod(n\\,{stride})),scale=w='if(gte(iw,ih),min(640,iw),trunc(iw*min(1,640/ih)))':h='if(gte(iw,ih),trunc(ih*min(1,640/iw)),min(640,ih))':flags=area";
        var r = await Run(["-y", .. SafeInput, "-i", Path.Combine(folder, "video"), "-vf", filter, "-fps_mode", "vfr", "-frames:v", Math.Max(600, MaxFrames * 4).ToString(CultureInfo.InvariantCulture), Path.Combine(output, "%04d.png")], ct);
        var all = Directory.GetFiles(output, "*.png").Order().ToArray();
        if (r.Code != 0 || all.Length == 0) throw new UserError("Could not extract video frames.", 422);
        var indices = all.Length <= MaxFrames ? Enumerable.Range(0, all.Length).ToArray() :
            Enumerable.Range(0, MaxFrames).Select(i => (int)Math.Round(i * (all.Length - 1d) / (MaxFrames - 1), MidpointRounding.ToEven)).Distinct().ToArray();
        return (indices.Select(i => all[i]).ToArray(), indices.Select(i => i * stride / fps).ToArray());
    }
}