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
    private readonly AlertJournal journal;

    public NotificationDispatcher(IEnumerable<INotifier> notifiers, INotificationResultSink sink,
        IOptions<NotificationOptions> options, ILogger<NotificationDispatcher> logger, AlertJournal? journal = null)
    {
        this.notifiers = notifiers.ToList(); this.sink = sink; this.options = options.Value; this.logger = logger;
        this.journal = journal ?? new AlertJournal(null, this.options);
        queue = Channel.CreateBounded<NotificationPayload>(new BoundedChannelOptions(this.options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public string ReviewUrl => options.ReviewBaseUrl;

    public IReadOnlyList<string> EnabledChannels() => notifiers.Where(n => n.Enabled).Select(n => n.Name).ToList();

    // Alerts accepted but not yet delivered (waiting, held for a digest, or being retried).
    internal int PendingAlerts => journal.Count;

    // Every alert is written to the journal before it is queued, so it survives a restart.
    private void Accept(NotificationPayload payload)
    {
        var keyed = payload with { Key = Guid.NewGuid().ToString("N") };
        journal.Add(keyed);
        queue.Writer.TryWrite(keyed);
    }

    public void EnqueueSystem(NotificationPayload payload) => Accept(payload);

    public void EnqueueFailure(VideoJob job)
    {
        var reason = job.Error ?? "Analisis gagal.";
        if (reason.Length > options.MaxDescriptionChars) reason = reason[..options.MaxDescriptionChars] + "…";
        Accept(new NotificationPayload(job.Id, job.CameraName ?? "(kamera tidak diketahui)",
            job.SegmentStart ?? job.Created, reason, false, false,
            $"{options.ReviewBaseUrl.TrimEnd('/')}/#job={job.Id}", 0, AlertKind.JobFailed));
    }

    public void Enqueue(VideoJob job, AgentDecision? decision = null)
    {
        var description = job.Description ?? "";
        if (description.Length > options.MaxDescriptionChars) description = description[..options.MaxDescriptionChars] + "…";
        var ignored = decision?.Action == AgentAction.Ignore;
        var channels = ignored ? AgentPolicy.RouteIgnored(EnabledChannels()) ?? new[] { "log" } : AgentPolicy.Route(decision);
        // An ignored flag is always low urgency, whatever the agent wrote, so it can neither skip the throttle
        // nor carry a [HIGH] subject.
        var urgency = ignored ? AgentUrgency.Low : decision?.Urgency;
        var summary = ignored
            ? "Diabaikan agen (prioritas rendah): " + (string.IsNullOrWhiteSpace(decision!.Reason) ? "tanpa alasan." : decision.Reason)
            : decision?.Summary;
        if (summary is { Length: > AgentPolicy.MaxSummaryChars }) summary = summary[..AgentPolicy.MaxSummaryChars] + "…";
        Accept(new NotificationPayload(job.Id, job.CameraName ?? "(kamera tidak diketahui)",
            job.SegmentStart ?? job.Created, description, job.Suspicious == "yes", job.Weapon == "yes",
            $"{options.ReviewBaseUrl.TrimEnd('/')}/#job={job.Id}", 0, AlertKind.Flag,
            urgency, summary, channels));
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
        // Alerts that were still waiting when the app last stopped go through the normal path again.
        foreach (var recovered in journal.Recovered) queue.Writer.TryWrite(recovered);
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // A critical alert is never held back by the per-camera throttle. A weapon flag counts as
                    // critical even when no agent set an urgency (agent off, notebook mode, no WhatsApp/email).
                    if (payload.Kind is AlertKind.CameraOffline or AlertKind.CameraRecovered || IsCritical(payload))
                    {
                        await Deliver(payload, [payload], stoppingToken);
                        continue;
                    }
                    bool sendNow;
                    lock (gate)
                    {
                        // Separate throttle per kind, so a failure digest never swallows a flag alert.
                        // Alerts routed to different channels are throttled separately, so a digest never drops a channel.
                        var key = $"{payload.Kind}|{payload.CameraName}|{string.Join(',', payload.Channels ?? Array.Empty<string>())}";
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
                    try { await Deliver(MostSevere(group) with { MergedCount = group.Count - 1 }, group, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Digest dispatch failed"); }
                }
                await RetryDue(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Channels that failed on every attempt are tried again here, with growing pauses, until the retry window ends.
    private async Task RetryDue(CancellationToken ct)
    {
        journal.Expire(DateTimeOffset.UtcNow);
        foreach (var due in journal.DueRetries(DateTimeOffset.UtcNow))
        {
            var covered = due.CoveredJobIds.Select(id => due.Message with { JobId = id }).ToList();
            if (covered.Count == 0) covered.Add(due.Message);
            try { await Deliver(due.Message, covered, ct, due.Key); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Retry failed for {Job}", due.Message.JobId); }
        }
    }

    private async Task Deliver(NotificationPayload message, List<NotificationPayload> covered, CancellationToken ct, string? retryKey = null)
    {
        var active = notifiers.Where(n => n.Enabled && (message.Channels is null || message.Channels.Contains(n.Name))).ToList();
        var sentBy = new HashSet<string>();
        var failures = new List<string>();
        foreach (var notifier in active)
        {
            string? error = null; var sent = false;
            for (var attempt = 1; attempt <= options.MaxAttempts && !sent; attempt++)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(options.SendTimeout);
                try { await notifier.SendAsync(message, limit.Token); sent = true; }
                // A timeout is also an OperationCanceledException. Only a real shutdown (ct) may escape from here:
                // anything else used to end the dispatcher loop for good.
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    error = ex is OperationCanceledException ? $"timed out after {options.SendTimeout.TotalSeconds:0}s" : Describe(ex);
                    logger.LogWarning("Notification via {Channel} attempt {Attempt}/{Max} failed for {Job}: {Error}", notifier.Name, attempt, options.MaxAttempts, message.JobId, error);
                    if (attempt < options.MaxAttempts) await Task.Delay(options.BackoffBase * Math.Pow(2, attempt - 1), ct);
                }
            }
            if (sent) sentBy.Add(notifier.Name); else failures.Add($"{notifier.Name}: {error}");
        }
        var real = active.Where(n => n.Name != "log").ToList();
        var ok = real.Count == 0 ? sentBy.Contains("log") : real.Any(n => sentBy.Contains(n.Name));
        var summary = failures.Count > 0 ? string.Join(" | ", failures) : null;
        // Delivered everywhere it could be: nothing left to do. Otherwise keep it, addressed only to the channels
        // that failed, so a retry never repeats a message on a channel that already got it.
        var failedChannels = real.Where(n => !sentBy.Contains(n.Name)).Select(n => n.Name).ToList();
        var keys = covered.Select(c => c.Key).Append(retryKey);
        if (failedChannels.Count == 0) journal.Complete(keys);
        else if (!journal.Fail(message with { Channels = failedChannels }, covered, retryKey))
            logger.LogError("Alert for {Job} could not be delivered on {Channels} and was dropped", message.JobId, string.Join(", ", failedChannels));
        foreach (var item in covered.Where(i => !string.IsNullOrEmpty(i.JobId)))
            await sink.RecordAsync(item.JobId, ok ? DateTimeOffset.UtcNow : null, summary, ct);
    }

    private static bool IsCritical(NotificationPayload p) =>
        p.Urgency == AgentUrgency.Critical || (p.IsModelFlag && p.Weapon);

    private static int Rank(string? urgency) => urgency switch
    {
        AgentUrgency.Critical => 3, AgentUrgency.High => 2, AgentUrgency.Medium => 1, _ => 0
    };

    // The digest speaks for every held alert, so it carries the most serious one (weapon, then urgency,
    // then suspicious). Among equals the newest wins, as before.
    internal static NotificationPayload MostSevere(IReadOnlyList<NotificationPayload> group) =>
        group.Select((payload, index) => (payload, index))
            .OrderByDescending(x => x.payload.Weapon)
            .ThenByDescending(x => Rank(x.payload.Urgency))
            .ThenByDescending(x => x.payload.Suspicious)
            .ThenByDescending(x => x.index)
            .First().payload;

    private static string Describe(Exception ex) => ex switch
    {
        NotifierException => ex.Message,
        HttpRequestException { StatusCode: { } code } => $"HttpRequestException {(int)code}",
        _ => ex.GetType().Name
    };
}