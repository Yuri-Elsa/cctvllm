using Xunit;

public class CalibrationTests
{
    private static VideoJob J(string id, double? score, string? review = null, string? suspicious = null, string? camera = null,
        string status = "completed", string weapon = "no", bool noPeople = false, string model = "qwen") => new()
    {
        Id = id, Name = id + ".mp4", Status = status, Weapon = weapon, NoPeople = noPeople, CameraName = camera, Model = model,
        Suspicious = suspicious ?? (score >= 0.5 ? "yes" : "no"), ReviewState = review,
        Screening = score is null ? null : new ScreeningLog { SuspiciousScore = score }
    };

    // Real events: 0.9 0.8 0.7 0.4 (confirmed) and 0.3 (missed). Normal: 0.6 0.55 (false alarm) and 0.2 0.1 0.05 (normal).
    private static List<VideoJob> Reviewed() =>
    [
        J("c1", 0.9, "confirmed"), J("c2", 0.8, "confirmed"), J("c3", 0.7, "confirmed"), J("c4", 0.4, "confirmed"), J("m1", 0.3, "missed"),
        J("f1", 0.6, "false_alarm"), J("f2", 0.55, "false_alarm"), J("n1", 0.2, "normal"), J("n2", 0.1, "normal"), J("n3", 0.05, "normal")
    ];

    [Fact]
    public void Labels_are_counted_as_real_events_and_normal_videos()
    {
        var report = Calibration.Compute(Reviewed(), null, true, 0.5);

        Assert.Equal((10, 10, 5, 5), (report.Considered, report.Used, report.Positives, report.Negatives));
        Assert.Equal((0, 4, 0), (report.AssumedNormal, report.CheckedUnflagged, report.PendingReview));
    }

    [Fact]
    public void The_current_threshold_is_scored_the_way_Scoring_Decide_would_flag_things()
    {
        // threshold 0.5 flags 0.9 0.8 0.7 (right) and 0.6 0.55 (wrong); it misses 0.4 and 0.3
        var current = Calibration.Compute(Reviewed(), null, true, 0.5).Current!;

        Assert.Equal((3, 2, 2, 3), (current.TruePositive, current.FalsePositive, current.FalseNegative, current.TrueNegative));
        Assert.Equal(0.6, current.Precision!.Value, 6);
        Assert.Equal(0.6, current.Recall!.Value, 6);
        Assert.Equal(0.6, current.F1!.Value, 6);
        Assert.Equal(0.4, current.FalseAlarmRate!.Value, 6);
    }

    [Fact]
    public void A_score_exactly_on_the_threshold_is_flagged()
    {
        var report = Calibration.Compute([J("a", 0.5, "confirmed"), J("b", 0.2, "normal")], null, true, 0.5);
        Assert.Equal(1, report.Current!.TruePositive);
    }

    [Fact]
    public void The_curve_runs_from_0_to_1_in_steps_of_0_01()
    {
        var curve = Calibration.Compute(Reviewed(), null, true, 0.5).Curve;

        Assert.Equal(101, curve.Count);
        Assert.Equal(0.0, curve[0].Threshold);
        Assert.Equal(1.0, curve[^1].Threshold);
        Assert.Equal((5, 5), (curve[0].TruePositive, curve[0].FalsePositive));     // threshold 0 flags everything
        Assert.Equal((0, 0), (curve[^1].TruePositive, curve[^1].FalsePositive));   // threshold 1 flags (almost) nothing
    }

    [Fact]
    public void The_recommendations_are_the_ones_worked_out_by_hand()
    {
        var picks = Calibration.Compute(Reviewed(), null, true, 0.5).Picks.ToDictionary(p => p.Key);

        // best F1 = 10/12 for every threshold in (0.2, 0.3]; the middle of that plateau is 0.25
        Assert.Equal(0.25, picks["balanced"].Point.Threshold);
        Assert.Equal(10.0 / 12, picks["balanced"].Point.F1!.Value, 6);
        // all five events are still found at 0.30 (the missed one scored 0.3); that is the highest such threshold
        Assert.Equal(0.30, picks["recall"].Point.Threshold);
        Assert.Equal((5, 2), (picks["recall"].Point.TruePositive, picks["recall"].Point.FalsePositive));
        // the false alarms scored 0.6 and 0.55, so the first threshold with no false alarm is 0.61
        Assert.Equal(0.61, picks["precision"].Point.Threshold);
        Assert.Equal(1.0, picks["precision"].Point.Precision!.Value, 6);
    }

    [Fact]
    public void Unflagged_and_unreviewed_videos_count_as_normal_only_when_asked()
    {
        var jobs = Reviewed();
        jobs.Add(J("u1", 0.15, null, "no"));

        var assume = Calibration.Compute(jobs, null, true, 0.5);
        Assert.Equal((11, 6, 1), (assume.Used, assume.Negatives, assume.AssumedNormal));

        var strict = Calibration.Compute(jobs, null, false, 0.5);
        Assert.Equal((10, 5, 0, 1), (strict.Used, strict.Negatives, strict.AssumedNormal, strict.PendingReview));
    }

    [Fact]
    public void A_flagged_video_nobody_reviewed_is_left_out_not_assumed_normal()
    {
        var jobs = Reviewed();
        jobs.Add(J("p1", 0.65, null, "yes"));

        var report = Calibration.Compute(jobs, null, true, 0.5);

        Assert.Equal(1, report.PendingReview);
        Assert.Equal(10, report.Used);
    }

    [Fact]
    public void Videos_that_cannot_be_scored_are_left_out_and_counted()
    {
        var jobs = Reviewed();
        jobs.Add(J("noscore", null, "confirmed"));                          // model answer only
        jobs.Add(J("nobody", 0.0, null, "no", noPeople: true));             // never reached the model
        jobs.Add(J("gun", 0.9, "confirmed", "yes", weapon: "yes"));         // the weapon flag has no threshold
        jobs.Add(J("busy", 0.9, "confirmed", status: "processing"));        // not finished

        var report = Calibration.Compute(jobs, null, true, 0.5);

        Assert.Equal((1, 1, 1), (report.NoScore, report.NoPeople, report.WeaponFlagged));
        Assert.Equal(13, report.Considered);                                // everything completed, the processing one is ignored
        Assert.Equal(10, report.Used);
    }

    [Fact]
    public void A_camera_can_be_calibrated_on_its_own()
    {
        var jobs = new List<VideoJob>
        {
            J("a", 0.9, "confirmed", camera: "lobby"), J("b", 0.1, "normal", camera: "lobby"), J("c", 0.8, "false_alarm", camera: "gudang")
        };

        var lobby = Calibration.Compute(jobs, "lobby", true, 0.5);

        Assert.Equal((2, 1, 1), (lobby.Used, lobby.Positives, lobby.Negatives));
    }

    [Fact]
    public void Too_few_labels_and_unchecked_unflagged_videos_come_with_warnings()
    {
        var onlyFlagged = new List<VideoJob> { J("a", 0.9, "confirmed"), J("b", 0.6, "false_alarm") };

        var warnings = string.Join("\n", Calibration.Compute(onlyFlagged, null, true, 0.5).Warnings);

        Assert.Contains("1 real event", warnings);
        Assert.Contains("1 normal video", warnings);
        Assert.Contains("No unflagged video has been checked", warnings);
    }

    [Fact]
    public void Scores_from_different_models_are_reported_as_mixed()
    {
        var jobs = new List<VideoJob> { J("a", 0.9, "confirmed", model: "qwen3-vl:4b"), J("b", 0.1, "normal", model: "llava") };

        Assert.Contains("2 different models", string.Join("\n", Calibration.Compute(jobs, null, true, 0.5).Warnings));
    }

    [Fact]
    public void With_nothing_reviewed_there_is_no_curve_to_trust_and_no_recommendation()
    {
        var report = Calibration.Compute([], null, true, 0.5);

        Assert.Null(report.Current);
        Assert.Empty(report.Picks);
        Assert.Contains("Nothing to calibrate", string.Join("\n", report.Warnings));
    }

    [Fact]
    public void Recommendations_need_both_real_events_and_normal_videos()
    {
        var onlyEvents = new List<VideoJob> { J("a", 0.9, "confirmed"), J("b", 0.8, "confirmed") };

        Assert.Empty(Calibration.Compute(onlyEvents, null, true, 0.5).Picks);
    }

    [Fact]
    public void The_curve_exports_as_csv_with_a_header_and_one_row_per_threshold()
    {
        var bytes = Calibration.ToCsv(Calibration.Compute(Reviewed(), null, true, 0.5));
        var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("threshold,true_positive,false_positive,false_negative,true_negative,precision,recall,f1,false_alarm_rate", lines[0]);
        Assert.Equal(102, lines.Length);
        Assert.StartsWith("0.5,3,2,2,3,0.6,0.6,0.6,0.4", lines[51]);       // numbers use '.' whatever the machine's language
    }

    [Fact]
    public void Videos_that_were_not_flagged_can_be_reviewed_as_missed_or_normal()
    {
        using var app = new TestApp();
        var job = new VideoJob { Id = Guid.NewGuid().ToString("N"), Name = "v.mp4", Status = "completed", Suspicious = "no" };
        app.Store.Add(job);

        app.Store.SetReview(job.Id, "missed");
        Assert.Equal("missed", app.Store.Get(job.Id).ReviewState);
        app.Store.SetReview(job.Id, "normal");
        Assert.Equal("normal", app.Store.Get(job.Id).ReviewState);
        Assert.Throws<UserError>(() => app.Store.SetReview(job.Id, "maybe"));
    }
}
