using System.Globalization;

public sealed class HealthOptions
{
    public TimeSpan CheckEvery => TimeSpan.FromSeconds(Math.Max(5, Number("CCTV_HEALTH_INTERVAL", 30)));

    public int ModelFailures => (int)Math.Max(1, Number("CCTV_MODEL_DOWN_CHECKS", 2));

    public int BacklogThreshold => (int)Math.Max(0, Number("CCTV_BACKLOG_ALERT", 50));

    public TimeSpan BacklogFor => TimeSpan.FromMinutes(Math.Max(0, Number("CCTV_BACKLOG_MINUTES", 10)));

    public TimeSpan Reminder => TimeSpan.FromMinutes(Math.Max(0, Number("CCTV_HEALTH_REMINDER_MINUTES", 30)));

    private static double Number(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}