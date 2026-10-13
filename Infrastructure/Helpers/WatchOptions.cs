// Read from environment variables, the same way NotificationOptions is.
public sealed class WatchOptions
{
    // Turns the folder watcher on. It watches the folder linked in the UI.
    public bool Enabled => Flag("CCTV_WATCH");

    // Start the queue by itself when the watcher adds videos. Without this the queue stops
    // when it empties (QueueStore.Take) and new videos wait until someone presses Start.
    public bool AutoRun => Flag("CCTV_AUTORUN");

    public TimeSpan PollInterval => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CCTV_WATCH_INTERVAL"), out var seconds) && seconds >= 2 ? seconds : 10);

    private static bool Flag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
