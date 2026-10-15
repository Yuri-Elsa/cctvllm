using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public class NotificationDispatcherTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeNotifier Notifier { get; }
        public FakeSink Sink { get; } = new();
        public NotificationDispatcher Dispatcher { get; }

        public Harness(NotificationOptions options, FakeNotifier? notifier = null, params INotifier[] more)
        {
            Notifier = notifier ?? new FakeNotifier();
            Dispatcher = new NotificationDispatcher([Notifier, .. more], Sink, Options.Create(options), NullLogger<NotificationDispatcher>.Instance);
        }

        public Task StartAsync() => Dispatcher.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.StopAsync(CancellationToken.None);
            Dispatcher.Dispose();
        }
    }

    private static NotificationOptions Opts(TimeSpan? window = null, int capacity = 100, int attempts = 3) => new()
    {
        ThrottleWindow = window ?? TimeSpan.FromMinutes(5),
        FlushInterval = TimeSpan.FromMilliseconds(20),
        BackoffBase = TimeSpan.FromMilliseconds(1),
        MaxAttempts = attempts,
        QueueCapacity = capacity
    };

    private static VideoJob Flagged(string camera) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "v.mp4",
        CameraName = camera,
        Status = "completed",
        Suspicious = "yes",
        Weapon = "no",
        Description = "Seseorang berdiri di dekat pintu.",
        SegmentStart = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task First_flag_for_a_camera_is_sent_immediately_and_recorded_on_the_job()
    {
        await using var h = new Harness(Opts());
        await h.StartAsync();
        var job = Flagged("lobby");
        h.Dispatcher.Enqueue(job);

        await Wait.Until(() => h.Notifier.Sent.Count == 1);
        await Wait.Until(() => h.Sink.Records.Count == 1);
        var record = h.Sink.Records.Single();
        Assert.Equal(job.Id, record.JobId);
        Assert.NotNull(record.NotifiedAt);
        Assert.Null(record.Error);
    }

    [Fact]
    public async Task Flags_for_one_camera_inside_the_window_are_merged_into_a_single_digest()
    {
        await using var h = new Harness(Opts(window: TimeSpan.FromMilliseconds(600)));
        await h.StartAsync();
        var a = Flagged("lobby"); var b = Flagged("lobby"); var c = Flagged("lobby");
        h.Dispatcher.Enqueue(a); h.Dispatcher.Enqueue(b); h.Dispatcher.Enqueue(c);

        await Wait.Until(() => h.Notifier.Sent.Count == 1);
        await Task.Delay(150);
        Assert.Single(h.Notifier.Sent);               // b and c are held, not sent

        await Wait.Until(() => h.Notifier.Sent.Count == 2);
        var digest = h.Notifier.Sent.Last();
        Assert.Equal(c.Id, digest.JobId);             // the digest shows the newest held alert...
        Assert.Equal(1, digest.MergedCount);          // ...plus a count of the others it stands for
        await Task.Delay(150);
        Assert.Equal(2, h.Notifier.Sent.Count);       // and nothing more is sent afterwards

        await Wait.Until(() => h.Sink.Records.Count == 3);
        var recorded = h.Sink.Records.ToDictionary(r => r.JobId);
        Assert.NotNull(recorded[b.Id].NotifiedAt);    // every merged job is marked as notified
        Assert.NotNull(recorded[c.Id].NotifiedAt);
    }

    [Fact]
    public async Task Cameras_are_throttled_independently()
    {
        await using var h = new Harness(Opts());
        await h.StartAsync();
        h.Dispatcher.Enqueue(Flagged("lobby"));
        h.Dispatcher.Enqueue(Flagged("gudang"));
        await Wait.Until(() => h.Notifier.Sent.Count == 2);
    }

    [Fact]
    public async Task A_failure_alert_does_not_swallow_a_flag_alert_for_the_same_camera()
    {
        await using var h = new Harness(Opts());
        await h.StartAsync();
        h.Dispatcher.Enqueue(Flagged("lobby"));
        h.Dispatcher.EnqueueFailure(Flagged("lobby") with { Status = "failed", Error = "model down" });
        await Wait.Until(() => h.Notifier.Sent.Count == 2);
        Assert.Contains(h.Notifier.Sent, p => p.Kind == AlertKind.Flag);
        Assert.Contains(h.Notifier.Sent, p => p.Kind == AlertKind.JobFailed);
    }

    [Fact]
    public async Task Camera_offline_alerts_bypass_the_throttle()
    {
        await using var h = new Harness(Opts());
        await h.StartAsync();
        NotificationPayload Offline() => new("", "lobby", DateTimeOffset.UtcNow, "tidak ada data", false, false, "http://x", 0, AlertKind.CameraOffline);
        h.Dispatcher.EnqueueSystem(Offline());
        h.Dispatcher.EnqueueSystem(Offline());
        await Wait.Until(() => h.Notifier.Sent.Count == 2);
    }

    [Fact]
    public async Task The_queue_is_bounded_and_drops_the_oldest_alerts()
    {
        await using var h = new Harness(Opts(capacity: 3));
        // Queued before the dispatcher runs, so nothing is read yet: five writes into three slots.
        foreach (var camera in new[] { "c1", "c2", "c3", "c4", "c5" }) h.Dispatcher.Enqueue(Flagged(camera));
        await h.StartAsync();

        await Wait.Until(() => h.Notifier.Sent.Count == 3);
        await Task.Delay(100);
        Assert.Equal(new[] { "c3", "c4", "c5" }, h.Notifier.Sent.Select(p => p.CameraName).ToArray());
    }

    [Fact]
    public async Task A_failing_channel_is_retried_before_the_job_is_marked_notified()
    {
        await using var h = new Harness(Opts(attempts: 3), new FakeNotifier(failTimes: 2));
        await h.StartAsync();
        h.Dispatcher.Enqueue(Flagged("lobby"));

        await Wait.Until(() => h.Sink.Records.Count == 1);
        Assert.Equal(3, h.Notifier.Attempts);
        Assert.Single(h.Notifier.Sent);
        Assert.NotNull(h.Sink.Records.Single().NotifiedAt);
    }

    [Fact]
    public async Task When_every_attempt_fails_the_error_is_stored_and_the_job_is_not_marked_notified()
    {
        await using var h = new Harness(Opts(attempts: 2), new FakeNotifier(failTimes: -1));
        await h.StartAsync();
        h.Dispatcher.Enqueue(Flagged("lobby"));

        await Wait.Until(() => h.Sink.Records.Count == 1);
        var record = h.Sink.Records.Single();
        Assert.Null(record.NotifiedAt);
        Assert.Equal("fake: boom", record.Error);
        Assert.Equal(2, h.Notifier.Attempts);
    }

    [Fact]
    public async Task Test_notification_reports_each_channel_and_skips_the_unconfigured_one()
    {
        var off = new FakeNotifier("off", enabled: false);
        await using var h = new Harness(Opts(), new FakeNotifier("on"), off);
        var results = await h.Dispatcher.SendTestAsync(CancellationToken.None);
        Assert.Equal("ok", results["on"]);
        Assert.Equal("not configured", results["off"]);
        Assert.Equal(0, off.Attempts);
    }
}