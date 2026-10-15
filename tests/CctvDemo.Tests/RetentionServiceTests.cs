using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

public class RetentionServiceTests
{
    private sealed class CapturingLogger : ILogger<RetentionService>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"[{logLevel}] {formatter(state, exception)}" + (exception is null ? "" : "\n" + exception));
    }

    private static async Task<string> Sweep(TestApp app)
    {
        var logger = new CapturingLogger();
        using var service = new RetentionService(app.Store, app.Rtsp, Options.Create(new RetentionOptions()), logger);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        return string.Join("\n", logger.Lines);
    }

    private static VideoJob People(VideoJob j) => j with { NoPeople = false, Suspicious = "no", Weapon = "no" };
    private static VideoJob Flag(VideoJob j) => j with { NoPeople = false, Suspicious = "yes", Weapon = "no" };
    private static bool InStore(TestApp app, VideoJob j) => app.Store.Jobs().Any(x => x.Id == j.Id);

    [Fact]
    public async Task Empty_segments_are_removed_after_their_short_limit_and_not_before()
    {
        using var app = new TestApp();
        var now = DateTimeOffset.UtcNow;
        var old = app.Segment("lobby", "20260101_100000.mp4", now.AddMinutes(-11), j => j with { NoPeople = true });
        var fresh = app.Segment("lobby", "20260101_110000.mp4", now.AddMinutes(-5), j => j with { NoPeople = true });

        var log = await Sweep(app);

        Assert.False(InStore(app, old), log);
        Assert.False(File.Exists(old.SourcePath!), log);
        Assert.True(InStore(app, fresh), log);
        Assert.True(File.Exists(fresh.SourcePath!), log);
    }

    [Fact]
    public async Task Unflagged_segments_with_people_are_kept_for_the_unflagged_limit()
    {
        using var app = new TestApp();
        var now = DateTimeOffset.UtcNow;
        var old = app.Segment("lobby", "20260101_100000.mp4", now.AddHours(-25), People);
        var recent = app.Segment("lobby", "20260101_110000.mp4", now.AddHours(-23), People);

        var log = await Sweep(app);

        Assert.False(InStore(app, old), log);
        Assert.True(InStore(app, recent), log);
    }

    [Fact]
    public async Task Flagged_segments_outlive_the_unflagged_limit_and_go_only_after_the_flagged_limit()
    {
        using var app = new TestApp();
        var now = DateTimeOffset.UtcNow;
        var twoDays = app.Segment("lobby", "20260101_100000.mp4", now.AddDays(-2), Flag);
        var ancient = app.Segment("lobby", "20260101_110000.mp4", now.AddDays(-31), Flag);

        var log = await Sweep(app);

        Assert.True(InStore(app, twoDays), log);
        Assert.True(File.Exists(twoDays.SourcePath!), log);
        Assert.False(InStore(app, ancient), log);
    }

    [Fact]
    public async Task A_video_outside_the_rtsp_folder_is_never_deleted_however_old()
    {
        using var app = new TestApp();
        var dir = Path.Combine(app.Dir, "my-videos", "lobby");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "20260101_100000.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        var job = new VideoJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "x.mp4",
            SourcePath = path,
            CameraName = "lobby",
            Status = "completed",
            NoPeople = true,
            Created = DateTimeOffset.UtcNow.AddDays(-90)
        };
        Directory.CreateDirectory(app.Store.Folder(job.Id));
        app.Store.Add(job);

        var log = await Sweep(app);

        Assert.True(File.Exists(path), log);
        Assert.True(InStore(app, job), log);
    }

    [Fact]
    public async Task When_a_camera_has_too_many_waiting_videos_the_oldest_are_dropped_but_their_files_stay()
    {
        using var app = new TestApp(("CCTV_MAX_QUEUE_PER_CAMERA", "2"));
        var now = DateTimeOffset.UtcNow;
        VideoJob Queued(string file, int minutesAgo) =>
            app.Segment("lobby", file, now.AddMinutes(-minutesAgo), j => j with { Status = "queued", Stage = "Queued" });
        var oldest = Queued("20260101_100000.mp4", 4);
        var older = Queued("20260101_100100.mp4", 3);
        var newer = Queued("20260101_100200.mp4", 2);
        var newest = Queued("20260101_100300.mp4", 1);

        var log = await Sweep(app);

        foreach (var dropped in new[] { oldest, older })
        {
            var job = app.Store.Get(dropped.Id);
            Assert.True(job.Status == "cancelled", $"{dropped.Name} is '{job.Status}', expected 'cancelled'.\n{log}");
            Assert.True(job.Stage == "Dropped (backlog)", $"{dropped.Name} stage is '{job.Stage}'.\n{log}");
            Assert.True(File.Exists(dropped.SourcePath!), log);
        }
        Assert.True(app.Store.Get(newer.Id).Status == "queued", log);
        Assert.True(app.Store.Get(newest.Id).Status == "queued", log);
    }

    [Fact]
    public async Task The_size_limit_removes_the_oldest_unflagged_segment_first_and_never_a_flagged_one()
    {
        using var app = new TestApp(("CCTV_MAX_DISK_GB", "0.0000121"));
        var now = DateTimeOffset.UtcNow;
        var oldestUnflagged = app.Segment("lobby", "20260101_100000.mp4", now.AddHours(-3), People, bytes: 6000);
        var flagged = app.Segment("lobby", "20260101_110000.mp4", now.AddHours(-2), Flag, bytes: 6000);
        var newestUnflagged = app.Segment("lobby", "20260101_120000.mp4", now.AddHours(-1), People, bytes: 6000);

        var log = await Sweep(app);

        Assert.False(InStore(app, oldestUnflagged), log);
        Assert.True(InStore(app, flagged), log);
        Assert.True(InStore(app, newestUnflagged), log); 
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_rtsp_folder_overlaps_the_linked_folder()
    {
        using var app = new TestApp();
        var old = app.Segment("lobby", "20260101_100000.mp4", DateTimeOffset.UtcNow.AddHours(-2), j => j with { NoPeople = true });
        app.Store.SetLinkedFolder(app.Rtsp.OutputRoot);

        var log = await Sweep(app);

        Assert.True(InStore(app, old), log);
        Assert.True(File.Exists(old.SourcePath!), log);
    }
}