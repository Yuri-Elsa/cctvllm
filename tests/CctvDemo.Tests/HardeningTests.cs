using System.Diagnostics;
using Xunit;

public class HardeningTests
{
    [Fact]
    public void Child_processes_do_not_inherit_CCTV_variables()
    {
        var info = new ProcessStartInfo("ffmpeg");
        info.Environment["CCTV_UI_PASSWORD"] = "secret";
        info.Environment["CCTV_RTSP_LOBBY"] = "rtsp://user:pw@host/x";
        info.Environment["cctv_api_key"] = "key";          // names are matched case-insensitively
        info.Environment["KEEP_ME"] = "1";

        ChildProcess.ScrubSecrets(info);

        Assert.DoesNotContain(info.Environment.Keys, k => k.StartsWith("CCTV_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1", info.Environment["KEEP_ME"]);
    }

    [Fact]
    public void A_corrupt_state_file_does_not_stop_startup_and_is_kept_for_inspection()
    {
        using var app = new TestApp();
        var data = Path.Combine(app.Dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.json"), "{ this is not json");

        var store = new QueueStore(new FakeEnv(app.Dir));

        Assert.Empty(store.Jobs());
        Assert.Single(Directory.GetFiles(data, "state.corrupt-*.json"));
    }

    [Fact]
    public void A_state_file_that_cannot_be_replaced_for_a_moment_does_not_fail_the_update_and_catches_up_later()
    {
        using var app = new TestApp();
        var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = "v.mp4", Status = "queued", Stage = "Queued" };
        app.Store.Add(job);
        var statePath = Path.Combine(app.Dir, "data", "state.json");

        using (new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None))   // another program holds the file
            app.Store.Update(job.Id, j => j with { Stage = "while locked" });                // must not throw

        Assert.Equal("while locked", app.Store.Get(job.Id).Stage);                             // the change is kept in memory
        app.Store.Update(job.Id, j => j with { Stage = "after unlock" });                      // and the next write saves everything
        Assert.Contains("after unlock", File.ReadAllText(statePath));
    }
}
