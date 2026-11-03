using System.Text.Json;
using Xunit;

public class SceneModeTests
{
    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => $"/f/{i:0000}.png").ToArray();
    private static double[] Times(int n) => Enumerable.Range(0, n).Select(i => i * 2.5).ToArray();

    [Fact]
    public void Scene_mode_is_off_by_default_and_old_settings_files_still_load()
    {
        var old = JsonSerializer.Deserialize<ModelSettings>("""{"mode":"local","personDetector":"yolo"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(old.SceneMode);
        Assert.Equal(4, old.SceneFrames);
    }

    [Fact]
    public void Fewer_frames_than_requested_are_all_kept()
    {
        var (paths, times) = DescriptionWorker.PickEvenly(Paths(3), Times(3), 4);
        Assert.Equal(Paths(3), paths);
        Assert.Equal(Times(3), times);
    }

    [Fact]
    public void Extra_frames_are_thinned_evenly_from_first_to_last()
    {
        var (paths, times) = DescriptionWorker.PickEvenly(Paths(9), Times(9), 4);
        Assert.Equal(4, paths.Length);
        Assert.Equal("/f/0000.png", paths[0]);
        Assert.Equal("/f/0008.png", paths[^1]);
        Assert.Equal(times.Order().ToArray(), times);
    }

    [Fact]
    public void A_single_requested_frame_comes_from_the_middle()
    {
        var (paths, _) = DescriptionWorker.PickEvenly(Paths(5), Times(5), 1);
        Assert.Equal(new[] { "/f/0002.png" }, paths);
    }
}