using Microsoft.Extensions.Options;

public sealed class CameraWatchdog(RtspIngestService rtsp, NotificationDispatcher notifications,
    IOptions<WatchdogOptions> options, ILogger<CameraWatchdog> logger) : BackgroundService
{
    private sealed class Track
    {
        public DateTimeOffset FirstSeen = DateTimeOffset.UtcNow;
        public bool Alerted;
        public DateTimeOffset LastAlert;
    }

    private readonly WatchdogOptions watch = options.Value;
    private readonly Dictionary<string, Track> tracks = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!rtsp.Enabled) return;
        logger.LogInformation("Camera watchdog on: offline after {Seconds}s without data, reminder every {Minutes} min.",
            watch.OfflineAfter.TotalSeconds, watch.Reminder.TotalMinutes);
        using var timer = new PeriodicTimer(watch.CheckEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { Check(DateTimeOffset.UtcNow); }
                catch (Exception ex) { logger.LogWarning(ex, "Camera watchdog check failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Check(DateTimeOffset now)
    {
        foreach (var camera in rtsp.Snapshot())
        {
            if (!tracks.TryGetValue(camera.Camera, out var track)) tracks[camera.Camera] = track = new Track { FirstSeen = now };

            var reference = camera.LastDataAt ?? track.FirstSeen;
            var offline = now - reference > watch.OfflineAfter;

            if (offline && !track.Alerted)
            {
                track.Alerted = true; track.LastAlert = now;
                Send(AlertKind.CameraOffline, camera, reference, Describe(camera, now - reference));
            }
            else if (offline && track.Alerted && watch.Reminder > TimeSpan.Zero && now - track.LastAlert >= watch.Reminder)
            {
                track.LastAlert = now;
                Send(AlertKind.CameraOffline, camera, reference, "Masih offline. " + Describe(camera, now - reference));
            }
            else if (!offline && track.Alerted && camera.State == "running")
            {
                track.Alerted = false;
                Send(AlertKind.CameraRecovered, camera, now, "Kamera mengirim data lagi.");
            }
        }
    }

    private static string Describe(CameraStatus camera, TimeSpan silent)
    {
        var text = $"Tidak ada data dari kamera selama {Math.Round(silent.TotalMinutes, 1)} menit. Percobaan ulang: {camera.Restarts}x.";
        return string.IsNullOrWhiteSpace(camera.LastError) ? text : $"{text} Error terakhir: {camera.LastError}";
    }

    private void Send(string kind, CameraStatus camera, DateTimeOffset time, string description)
    {
        logger.LogWarning("Camera {Camera}: {Kind}", camera.Camera, kind);
        notifications.EnqueueSystem(new NotificationPayload("", camera.Camera, time, description,
            false, false, notifications.ReviewUrl, 0, kind));
    }
}