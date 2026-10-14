using System.Threading.Channels;
using Microsoft.Extensions.Options;

public sealed class NotificationDispatcher : BackgroundService
{
    private sealed class Camera { public DateTimeOffset LastSent = DateTimeOffset.MinValue; public List<NotificationPayload> Held = []; }

    private readonly Channel<NotificationPayload> queue;
    private readonly IReadOnlyList<INotifier> notifiers;
    private readonly INotificationResultSink sink;
    private readonly NotificationOptions options;
    private readonly ILogger<NotificationDispatcher> logger;
    private readonly Dictionary<string, Camera> cameras = new();
    private readonly object gate = new();

    public NotificationDispatcher(IEnumerable<INotifier> notifiers, INotificationResultSink sink,
        IOptions<NotificationOptions> options, ILogger<NotificationDispatcher> logger)
    {
        this.notifiers = notifiers.ToList(); this.sink = sink; this.options = options.Value; this.logger = logger;
        queue = Channel.CreateBounded<NotificationPayload>(new BoundedChannelOptions(this.options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public string ReviewUrl => options.ReviewBaseUrl;

    public void EnqueueSystem(NotificationPayload payload) => queue.Writer.TryWrite(payload);

    public void EnqueueFailure(VideoJob job)
    {
        var reason = job.Error ?? "Analisis gagal.";
        if (reason.Length > options.MaxDescriptionChars) reason = reason[..options.MaxDescriptionChars] + "…";
        queue.Writer.TryWrite(new NotificationPayload(job.Id, job.CameraName ?? "(kamera tidak diketahui)",
            job.SegmentStart ?? job.Created, reason, false, false,
            $"{options.ReviewBaseUrl.TrimEnd('/')}/#job={job.Id}", 0, AlertKind.JobFailed));
    }

    public void Enqueue(VideoJob job)
    {
        var description = job.Description ?? "";
        if (description.Length > options.MaxDescriptionChars) description = description[..options.MaxDescriptionChars] + "…";
        queue.Writer.TryWrite(new NotificationPayload(job.Id, job.CameraName ?? "(kamera tidak diketahui)",
            job.SegmentStart ?? job.Created, description, job.Suspicious == "yes", job.Weapon == "yes",
            $"{options.ReviewBaseUrl.TrimEnd('/')}/#job={job.Id}"));
    }

    // Backs the "Test notification" button. Bypasses throttle and does not touch any job.
    public async Task<Dictionary<string, string>> SendTestAsync(CancellationToken ct)
    {
        var probe = new NotificationPayload("test", "Kamera uji", DateTimeOffset.UtcNow, "Notifikasi uji. Bukan kejadian nyata.",
            true, false, options.ReviewBaseUrl);
        var results = new Dictionary<string, string>();
        foreach (var notifier in notifiers)
        {
            if (!notifier.Enabled) { results[notifier.Name] = "not configured"; continue; }
            try { await notifier.SendAsync(probe, ct); results[notifier.Name] = "ok"; }
            catch (Exception ex) when (ex is not OperationCanceledException) { results[notifier.Name] = Describe(ex); }
        }
        return results;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flusher = FlushLoop(stoppingToken);
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (payload.Kind is AlertKind.CameraOffline or AlertKind.CameraRecovered)
                    {
                        await Deliver(payload, [payload], stoppingToken);
                        continue;
                    }
                    bool sendNow;
                    lock (gate)
                    {
                        // Separate throttle per kind, so a failure digest never swallows a flag alert.
                        var key = $"{payload.Kind}|{payload.CameraName}";
                        if (!cameras.TryGetValue(key, out var cam)) cameras[key] = cam = new Camera();
                        var now = DateTimeOffset.UtcNow;
                        sendNow = cam.Held.Count == 0 && now - cam.LastSent >= options.ThrottleWindow;
                        if (sendNow) cam.LastSent = now; else cam.Held.Add(payload);
                    }
                    if (sendNow) await Deliver(payload, [payload], stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Notification dispatch failed for {Job}", payload.JobId);
                }
            }
        }
        catch (OperationCanceledException) { }
        await flusher;
    }

    private async Task FlushLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var due = new List<List<NotificationPayload>>();
                lock (gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var cam in cameras.Values.Where(c => c.Held.Count > 0 && now - c.LastSent >= options.ThrottleWindow))
                    {
                        due.Add(cam.Held); cam.Held = []; cam.LastSent = now;
                    }
                }
                foreach (var group in due)
                {
                    try { await Deliver(group[^1] with { MergedCount = group.Count - 1 }, group, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Digest dispatch failed"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Deliver(NotificationPayload message, List<NotificationPayload> covered, CancellationToken ct)
    {
        var active = notifiers.Where(n => n.Enabled).ToList();
        string? error = null; var ok = false;
        for (var attempt = 1; attempt <= options.MaxAttempts && !ok; attempt++)
        {
            try
            {
                foreach (var notifier in active) await notifier.SendAsync(message, ct);
                ok = true; error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = Describe(ex);
                logger.LogWarning("Notification attempt {Attempt}/{Max} failed for {Job}: {Error}", attempt, options.MaxAttempts, message.JobId, error);
                if (attempt < options.MaxAttempts) await Task.Delay(options.BackoffBase * Math.Pow(2, attempt - 1), ct);
            }
        }
        foreach (var item in covered.Where(i => !string.IsNullOrEmpty(i.JobId)))
            await sink.RecordAsync(item.JobId, ok ? DateTimeOffset.UtcNow : null, error, ct);
    }

    private static string Describe(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code } ? $"HttpRequestException {(int)code}" : ex.GetType().Name;
}