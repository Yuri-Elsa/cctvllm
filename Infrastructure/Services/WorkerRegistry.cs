using System.Collections.Concurrent;

// Which remote workers have asked the coordinator for work lately. Only for display (System check, GET /api/workers).
public sealed class WorkerRegistry
{
    public sealed record WorkerInfo(string Name, DateTimeOffset LastSeen, string? JobId);

    private readonly ConcurrentDictionary<string, WorkerInfo> workers = new();

    public void Seen(string name, string? jobId = null) => workers[name] = new WorkerInfo(name, DateTimeOffset.UtcNow, jobId);

    public IReadOnlyList<WorkerInfo> All() => workers.Values.OrderBy(w => w.Name).ToArray();

    public IEnumerable<WorkerInfo> Active(TimeSpan window) => All().Where(w => DateTimeOffset.UtcNow - w.LastSeen <= window);
}

// Puts videos back in the queue when their remote worker goes silent (crashed, lost the network, was switched off).
public sealed class LeaseReaper(QueueStore store, ILogger<LeaseReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var count = store.RequeueExpired(DateTimeOffset.UtcNow);
                    if (count > 0) logger.LogWarning("{Count} video(s) went back to the queue: their worker stopped reporting.", count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Lease check failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
