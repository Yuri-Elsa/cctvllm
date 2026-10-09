public sealed class QueueNotificationSink(QueueStore store) : INotificationResultSink
{
    public Task RecordAsync(string jobId, DateTimeOffset? notifiedAt, string? error, CancellationToken ct)
    {
        store.Update(jobId, j => j with { NotifiedAt = notifiedAt, NotifyError = error });
        return Task.CompletedTask;
    }
}