public interface INotifier
{
    string Name { get; }
    bool Enabled { get; }
    Task SendAsync(NotificationPayload payload, CancellationToken ct);
}

public interface INotificationResultSink
{
    Task RecordAsync(string jobId, DateTimeOffset? notifiedAt, string? error, CancellationToken ct);
}