using Xunit;

public class QueueStoreReviewTests
{
    private static VideoJob Add(TestApp app, string status = "completed")
    {
        var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = "v.mp4", Status = status, Suspicious = "yes", Weapon = "no" };
        app.Store.Add(job);
        return job;
    }

    [Fact]
    public void A_verdict_is_stored_and_can_be_changed_or_cleared()
    {
        using var app = new TestApp();
        var job = Add(app);

        app.Store.SetReview(job.Id, "confirmed");
        Assert.Equal("confirmed", app.Store.Get(job.Id).ReviewState);
        app.Store.SetReview(job.Id, "false_alarm");
        Assert.Equal("false_alarm", app.Store.Get(job.Id).ReviewState);
        app.Store.SetReview(job.Id, null);
        Assert.Null(app.Store.Get(job.Id).ReviewState);
    }

    [Fact]
    public void An_unknown_verdict_is_rejected()
    {
        using var app = new TestApp();
        var job = Add(app);
        Assert.Throws<UserError>(() => app.Store.SetReview(job.Id, "maybe"));
        Assert.Null(app.Store.Get(job.Id).ReviewState);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("processing")]
    [InlineData("failed")]
    public void Only_a_finished_video_can_be_reviewed(string status)
    {
        using var app = new TestApp();
        var job = Add(app, status);
        var error = Assert.Throws<UserError>(() => app.Store.SetReview(job.Id, "confirmed"));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public void An_unknown_video_is_a_404()
    {
        using var app = new TestApp();
        var error = Assert.Throws<UserError>(() => app.Store.SetReview(Guid.NewGuid().ToString("N"), "confirmed"));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public void The_verdict_survives_a_restart()
    {
        using var app = new TestApp();
        var job = Add(app);
        app.Store.SetReview(job.Id, "false_alarm");

        var reloaded = new QueueStore(new FakeEnv(app.Dir)); 
        Assert.Equal("false_alarm", reloaded.Get(job.Id).ReviewState);
    }
}

public class PreflightModelMatchTests
{
    private static readonly string[] Installed = ["gemma4:e2b-it-qat", "llama3:latest", "qwen3-vl:4b-cctv"];

    [Theory]
    [InlineData("gemma4:e2b-it-qat", true)]
    [InlineData("qwen3-vl:4b-cctv", true)]
    [InlineData("llama3", true)]
    [InlineData("LLAMA3:Latest", true)]
    [InlineData("qwen3-vl", false)]
    [InlineData("gemma4:e4b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ModelInstalled_matches_the_way_ollama_names_models(string wanted, bool expected) =>
        Assert.Equal(expected, Preflight.ModelInstalled(Installed, wanted));

    [Fact]
    public void ModelInstalled_is_false_when_nothing_is_installed() =>
        Assert.False(Preflight.ModelInstalled([], "gemma4:e2b-it-qat"));
}