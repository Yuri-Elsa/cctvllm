using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public class FolderWatcherServiceTests
{
    private static FolderWatcherService Watcher(TestApp app) =>
        new(app.Store, null!, app.Rtsp, new ScanCoordinator(), Options.Create(new WatchOptions()), NullLogger<FolderWatcherService>.Instance);

    private static string NewFile(TestApp app, int bytes, string name = "clip.mp4")
    {
        var dir = Path.Combine(app.Dir, "watched", "lobby");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void A_file_is_ready_only_after_it_is_unchanged_between_two_polls()
    {
        using var app = new TestApp();
        var watcher = Watcher(app);
        var path = NewFile(app, 100);

        Assert.False(watcher.IsReady(path)); 
        Assert.True(watcher.IsReady(path)); 
    }

    [Fact]
    public void A_file_that_was_handled_is_not_offered_again()
    {
        using var app = new TestApp();
        var watcher = Watcher(app);
        var path = NewFile(app, 100);
        watcher.IsReady(path);
        Assert.True(watcher.IsReady(path));

        Assert.False(watcher.IsReady(path));
        Assert.False(watcher.IsReady(path));
    }

    [Fact]
    public void A_growing_file_is_not_ready_until_it_stops_growing()
    {
        using var app = new TestApp();
        var watcher = Watcher(app);
        var path = NewFile(app, 100);
        Assert.False(watcher.IsReady(path));

        File.AppendAllText(path, "more data");
        Assert.False(watcher.IsReady(path)); 

        Assert.True(watcher.IsReady(path));  
    }

    [Fact]
    public void A_file_that_changes_after_it_was_handled_is_picked_up_again()
    {
        using var app = new TestApp();
        var watcher = Watcher(app);
        var path = NewFile(app, 100);
        watcher.IsReady(path);
        Assert.True(watcher.IsReady(path));

        File.AppendAllText(path, "replaced with a longer recording");
        Assert.False(watcher.IsReady(path));
        Assert.True(watcher.IsReady(path));
    }

    [Fact]
    public void An_empty_file_is_never_ready()
    {
        using var app = new TestApp();
        var watcher = Watcher(app);
        var path = NewFile(app, 0);

        Assert.False(watcher.IsReady(path));
        Assert.False(watcher.IsReady(path));
        Assert.False(watcher.IsReady(path));
    }

    [Fact]
    public void A_missing_file_is_not_ready()
    {
        using var app = new TestApp();
        Assert.False(Watcher(app).IsReady(Path.Combine(app.Dir, "watched", "nope.mp4")));
    }

    [Fact]
    public void A_finished_rtsp_segment_is_ready_on_the_first_poll()
    {
        using var app = new TestApp();
        var job = app.Segment("lobby", "20260101_100000.mp4", DateTimeOffset.UtcNow, bytes: 50);
        Assert.True(Watcher(app).IsReady(job.SourcePath!));
    }
}