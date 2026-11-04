using System.Text.Json;
using Xunit;

// Scene mode: when the person detector finds nobody, a few evenly spaced frames still go to the model, so a weapon
// on a table or a fire with nobody near it can be flagged. SceneModeTests covers the defaults and the first PickEvenly cases;
// these cover the rest of what can be checked without ffmpeg and a model server.
public class SceneModeBehaviourTests
{
    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => $"/f/{i:0000}.png").ToArray();
    private static double[] Times(int n) => Enumerable.Range(0, n).Select(i => i * 2.5).ToArray();

    // ---- the instruction the model gets ----

    [Fact]
    public void The_scene_note_names_the_cases_that_never_reached_the_model_before()
    {
        var note = DescriptionWorker.SceneNote;
        Assert.Contains("pistol", note);      // a weapon on a table or floor
        Assert.Contains("api", note);         // fire
        Assert.Contains("asap", note);        // smoke
        Assert.Contains("weapon", note);
        Assert.Contains("Jangan mengarang orang", note);   // an empty room must not be answered with an invented person
        Assert.Contains("kosong dan tenang", note);        // and a quiet empty room must be allowed to be "no"
    }

    // ---- scene-mode scores must not leak into the person-threshold calibration ----

    private static VideoJob Job(string id, double score, string review, bool scene = false) => new()
    {
        Id = id, Name = id + ".mp4", Status = "completed", ReviewState = review,
        Suspicious = score >= 0.5 ? "yes" : "no", Weapon = "no",
        Screening = new ScreeningLog { SuspiciousScore = score, SceneMode = scene }
    };

    [Fact]
    public void Scene_mode_videos_are_left_out_of_the_calibration_curve_and_counted_with_the_empty_ones()
    {
        var jobs = new[]
        {
            Job("p1", 0.8, "confirmed"),
            Job("n1", 0.1, "normal"),
            Job("s1", 0.9, "confirmed", scene: true),   // an empty scene: its score is not a person score
            Job("s2", 0.2, "normal", scene: true)
        };

        var report = Calibration.Compute(jobs, null, true, 0.5);

        Assert.Equal(4, report.Considered);
        Assert.Equal(2, report.NoPeople);
        Assert.Equal(2, report.Used);
        Assert.Equal((1, 1), (report.Positives, report.Negatives));
    }

    [Fact]
    public void A_scene_mode_job_is_still_a_completed_job_and_not_marked_as_nobody_there()
    {
        // DescribeScene does not set NoPeople: the model did run on it. Only the screening log says it was scene mode.
        var job = new VideoJob { Id = "s", Name = "s.mp4", Status = "completed", NoPeople = false, Screening = new ScreeningLog { SceneMode = true } };
        Assert.False(job.NoPeople);
        Assert.True(job.Screening!.SceneMode);
        Assert.False(new ScreeningLog().SceneMode);
    }

    // ---- choosing the frames ----

    private static int IndexOf(string path) => int.Parse(Path.GetFileNameWithoutExtension(path));

    [Theory]
    [InlineData(50, 4)]
    [InlineData(9, 8)]
    [InlineData(100, 8)]
    [InlineData(5, 2)]
    public void Thinning_keeps_every_frame_paired_with_its_own_timestamp(int available, int wanted)
    {
        var (paths, times) = DescriptionWorker.PickEvenly(Paths(available), Times(available), wanted);

        Assert.Equal(paths.Length, times.Length);
        for (var i = 0; i < paths.Length; i++)
            Assert.Equal(IndexOf(paths[i]) * 2.5, times[i]);
    }

    [Fact]
    public void Thinning_never_repeats_a_frame_and_never_returns_more_than_asked()
    {
        for (var available = 1; available <= 40; available++)
            for (var wanted = 1; wanted <= 8; wanted++)
            {
                var (paths, times) = DescriptionWorker.PickEvenly(Paths(available), Times(available), wanted);

                Assert.Equal(Math.Min(available, wanted), paths.Length);
                Assert.Equal(paths.Distinct().Count(), paths.Length);
                Assert.Equal(times.Order().ToArray(), times);
                if (available > wanted && wanted >= 2)
                {
                    Assert.Equal("/f/0000.png", paths[0]);
                    Assert.Equal($"/f/{available - 1:0000}.png", paths[^1]);
                }
            }
    }

    // ---- the settings file the Gemma run uses ----

    private static string? FindFromBase(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void The_gemma_split_settings_file_turns_on_both_new_features_and_keeps_the_baseline_model()
    {
        var path = FindFromBase(Path.Combine("eval", "settings_gemma_split.json"));
        if (path is null) return;   // tests run from a copy without the eval folder

        var settings = JsonSerializer.Deserialize<ModelSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(settings.SeparateClassification);
        Assert.True(settings.SceneMode);
        Assert.Equal("gemma4:e2b-it-qat", settings.Model);   // same model as the baseline, so only the settings differ
        Assert.True(settings.WeaponEnabled);
        Assert.False(settings.BatchProcessing);
    }
}
