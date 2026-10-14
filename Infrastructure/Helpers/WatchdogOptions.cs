using System.Globalization;

public sealed class WatchdogOptions
{
    public TimeSpan OfflineAfter => TimeSpan.FromSeconds(Math.Max(15, Number("CCTV_CAMERA_OFFLINE_SECONDS", 60)));

    public TimeSpan Reminder => TimeSpan.FromMinutes(Math.Max(0, Number("CCTV_CAMERA_REMINDER_MINUTES", 30)));

    public TimeSpan CheckEvery => TimeSpan.FromSeconds(5);

    private static double Number(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}