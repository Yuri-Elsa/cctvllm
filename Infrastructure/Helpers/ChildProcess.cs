using System.Diagnostics;

public static class ChildProcess
{
    // ffmpeg, the YOLO script and the preflight probes never need the app's own settings, but a child
    // process inherits the whole environment by default: UI password, API key, WhatsApp token, SMTP
    // password and RTSP URLs. ffmpeg parses untrusted uploads and ultralytics may reach the network,
    // so nothing named CCTV_* is handed on.
    public static void ScrubSecrets(ProcessStartInfo info)
    {
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("CCTV_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
    }
}
