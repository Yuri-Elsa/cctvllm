using Microsoft.Extensions.Options;

public sealed class HealthTracker(HealthOptions options)
{
    private int modelFailures;
    private bool modelDown;
    private DateTimeOffset modelAlertAt;
    private DateTimeOffset? backlogSince;
    private bool backlogAlerted;
    private DateTimeOffset backlogAlertAt;

    public List<(string Kind, string Text)> Step(DateTimeOffset now, bool modelReachable, int queued, bool running, string endpoint)
    {
        var alerts = new List<(string Kind, string Text)>();

        if (modelReachable)
        {
            modelFailures = 0;
            if (modelDown) { modelDown = false; alerts.Add((AlertKind.ModelRecovered, "Server model bisa dihubungi lagi.")); }
        }
        else if (++modelFailures >= options.ModelFailures)
        {
            if (!modelDown)
            {
                modelDown = true; modelAlertAt = now;
                alerts.Add((AlertKind.ModelDown, $"Server model ({endpoint}) tidak bisa dihubungi. Video baru tidak dianalisis sampai server hidup lagi."));
            }
            else if (options.Reminder > TimeSpan.Zero && now - modelAlertAt >= options.Reminder)
            {
                modelAlertAt = now;
                alerts.Add((AlertKind.ModelDown, $"Server model ({endpoint}) masih tidak bisa dihubungi."));
            }
        }

        if (options.BacklogThreshold > 0)
        {
            if (queued > options.BacklogThreshold)
            {
                var since = backlogSince ??= now;
                var text = $"Antrean menumpuk: {queued} video menunggu (batas {options.BacklogThreshold}). Antrean {(running ? "berjalan" : "dijeda")}, server model {(modelReachable ? "terjangkau" : "tidak terjangkau")}.";
                if (!backlogAlerted && now - since >= options.BacklogFor)
                {
                    backlogAlerted = true; backlogAlertAt = now;
                    alerts.Add((AlertKind.QueueBacklog, text));
                }
                else if (backlogAlerted && options.Reminder > TimeSpan.Zero && now - backlogAlertAt >= options.Reminder)
                {
                    backlogAlertAt = now;
                    alerts.Add((AlertKind.QueueBacklog, "Masih menumpuk. " + text));
                }
            }
            else if (queued <= options.BacklogThreshold / 2)
            {
                backlogSince = null;
                if (backlogAlerted) { backlogAlerted = false; alerts.Add((AlertKind.QueueRecovered, $"Antrean kembali normal: {queued} video menunggu.")); }
            }
        }
        return alerts;
    }
}

public sealed class HealthMonitor(QueueStore store, IHttpClientFactory clients, NotificationDispatcher notifications,
    IOptions<HealthOptions> options, ILogger<HealthMonitor> logger) : BackgroundService
{
    private readonly HealthTracker tracker = new(options.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CheckEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var settings = store.Settings;
                    var reachable = await ModelReachable(settings.Endpoint, stoppingToken);
                    var queued = store.Jobs().Count(j => j.Status == "queued");
                    foreach (var (kind, text) in tracker.Step(DateTimeOffset.UtcNow, reachable, queued, store.Running, settings.Endpoint))
                    {
                        logger.LogWarning("Health {Kind}: {Text}", kind, text);
                        notifications.EnqueueSystem(new NotificationPayload("", "Sistem", DateTimeOffset.UtcNow, text, false, false, notifications.ReviewUrl, 0, kind));
                    }
                    Metrics.ModelUp = reachable ? 1 : 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Health check failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ModelReachable(string endpoint, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = ModelCalls.CreateClient(clients);
            using var response = await client.GetAsync(endpoint.TrimEnd('/') + "/models", limit.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested) { return false; }
    }
}