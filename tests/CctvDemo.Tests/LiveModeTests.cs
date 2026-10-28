using Xunit;

public class LiveModeTests
{
    [Fact]
    public void Live_mode_is_off_by_default_and_has_sensible_defaults()
    {
        using var env = new EnvScope(("CCTV_LIVE", null), ("CCTV_LIVE_PREROLL", null), ("CCTV_LIVE_POSTROLL", null), ("CCTV_LIVE_MAX_CLIP", null),
            ("CCTV_LIVE_FPS", null), ("CCTV_LIVE_SIZE", null), ("CCTV_LIVE_CONFIRM", null), ("CCTV_RTSP_STALL_SECONDS", null));
        var options = new RtspOptions();

        Assert.False(options.Live);
        Assert.Equal((5, 4, 20, 6, 960, 2), (options.LivePreRoll, options.LivePostRoll, options.LiveMaxClip, options.LiveFps, options.LiveSize, options.LiveConfirm));
        Assert.Equal(TimeSpan.FromSeconds(30), options.LiveStallAfter);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    public void Live_mode_is_switched_on_by_CCTV_LIVE(string value, bool expected)
    {
        using var env = new EnvScope(("CCTV_LIVE", value));
        Assert.Equal(expected, new RtspOptions().Live);
    }

    [Fact]
    public void Live_settings_are_kept_inside_safe_limits()
    {
        using var env = new EnvScope(("CCTV_LIVE_PREROLL", "999"), ("CCTV_LIVE_POSTROLL", "0"), ("CCTV_LIVE_MAX_CLIP", "1"),
            ("CCTV_LIVE_FPS", "100"), ("CCTV_LIVE_SIZE", "10"), ("CCTV_LIVE_CONFIRM", "0"), ("CCTV_RTSP_STALL_SECONDS", "1"));
        var options = new RtspOptions();

        Assert.Equal((30, 1, 5, 15, 320, 1), (options.LivePreRoll, options.LivePostRoll, options.LiveMaxClip, options.LiveFps, options.LiveSize, options.LiveConfirm));
        Assert.Equal(TimeSpan.FromSeconds(10), options.LiveStallAfter);
    }

    [Fact]
    public void The_watchers_json_lines_are_understood()
    {
        var beat = RtspIngestService.ParseLiveLine("""{"event": "hb", "people": 2, "recording": true}""");
        Assert.Equal(new RtspIngestService.LiveEvent("hb", 2, true, null, 0), beat!.Value);

        var clip = RtspIngestService.ParseLiveLine("""{"event": "clip", "file": "20261028_153000.mp4", "seconds": 14.5}""");
        Assert.Equal("clip", clip!.Value.Kind);
        Assert.Equal("20261028_153000.mp4", clip.Value.File);
        Assert.Equal(14.5, clip.Value.Seconds);

        Assert.Equal("person", RtspIngestService.ParseLiveLine("""{"event":"person","people":1}""")!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UserWarning: something from a library")]
    [InlineData("{ not json")]
    [InlineData("""{"no_event":1}""")]
    [InlineData("""{"event":5}""")]
    [InlineData("[1,2]")]
    public void Anything_that_is_not_a_watcher_event_is_ignored(string? line) =>
        Assert.Null(RtspIngestService.ParseLiveLine(line));

    [Fact]
    public async Task A_new_clip_wakes_the_folder_watcher_without_asking_for_a_linked_folder_scan()
    {
        var scans = new ScanCoordinator();
        scans.Wake();

        Assert.True(await scans.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));   // the watcher wakes at once
        Assert.False(scans.TryTake());                                                          // but no linked-folder scan was requested
    }

    [Fact]
    public void A_cameras_status_says_which_mode_it_is_in()
    {
        var segments = new CameraStatus("lobby", "running", null, 0, null);
        Assert.Equal("segments", segments.Mode);
        Assert.Null(segments.People);
        Assert.False(segments.Recording);
    }
}
