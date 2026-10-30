using System.Text.Json;
using Xunit;

public class HybridDetectorTests
{
    private const string Motion = """
        [{"timestamp":0.5,"people":1,"fastest":null,"motion":null,"uncertain":true},
         {"timestamp":1.0,"people":1,"fastest":0.4,"motion":"walking","uncertain":true},
         {"timestamp":1.5,"people":1,"fastest":0.5,"motion":"walking"},
         {"timestamp":3.0,"people":1,"fastest":2.6,"motion":"running","uncertain":true}]
        """;

    private static double[] Times(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetProperty("timestamp").GetDouble()).ToArray();

    [Fact]
    public void Uncertain_motion_is_dropped_when_the_llm_confirmed_nobody_nearby()
    {
        var kept = HybridPersonDetector.DropRejectedMotion(Motion, [1.0], 1.0);
        Assert.Equal(new[] { 0.5, 1.0, 1.5 }, Times(kept));
    }

    [Fact]
    public void Confident_motion_always_stays_and_the_uncertain_marker_is_removed()
    {
        var kept = HybridPersonDetector.DropRejectedMotion(Motion, [], 1.0);
        Assert.Equal(new[] { 1.5 }, Times(kept));
        Assert.DoesNotContain("uncertain", HybridPersonDetector.DropRejectedMotion(Motion, [1.0, 3.0], 1.0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public void Empty_motion_stays_an_empty_list(string motion) =>
        Assert.Equal("[]", HybridPersonDetector.DropRejectedMotion(motion, [1.0], 1.0));

    [Fact]
    public void Shape_keeps_the_shape_the_description_step_expects()
    {
        using var people = JsonDocument.Parse("""[{"id":null,"confidence":0.9}]""");
        var frame = new TrackedFrame("/a/0001.png", "/a/0001-plain.png", 1.5, people.RootElement.Clone(), 0.9, false);
        var d = PersonTracking.Shape([frame], "[]", []);
        Assert.Equal(new[] { "/a/0001.png" }, d.Paths);
        Assert.Equal(new[] { "/a/0001-plain.png" }, d.PlainPaths);
        Assert.Equal(new[] { 1.5 }, d.Timestamps);
        Assert.Equal(1.5, JsonDocument.Parse(d.Evidence).RootElement[0].GetProperty("timestamp").GetDouble());
        Assert.Equal(0, d.LlmChecked);
    }

    [Fact]
    public void New_settings_have_safe_defaults_and_old_settings_files_still_load()
    {
        var old = JsonSerializer.Deserialize<ModelSettings>("""{"mode":"local","personDetector":"llm","personThreshold":0.3}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(0.10, old.PersonVerifyLow);
        Assert.Equal(0.5, old.HybridLlmThreshold);
        Assert.Equal(2, old.LlmConcurrency);
    }
}