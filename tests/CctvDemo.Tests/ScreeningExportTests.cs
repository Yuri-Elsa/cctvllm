using System.Text;
using System.Text.Json;
using Xunit;

public class ScreeningExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static VideoJob Job(ScreeningLog? screening) => new()
    {
        Id = "a", Name = "a.mp4", CameraName = "lobby", Status = "completed", Stage = "Done", Duration = 60,
        SegmentStart = T0, Created = T0, Suspicious = "yes", Weapon = "no", Description = "d", Screening = screening
    };

    private static string[] Lines(params VideoJob[] jobs) =>
        Encoding.UTF8.GetString(JobExporter.ToCsv(jobs)[3..]).TrimEnd().Split("\r\n");

    // Splits one CSV line on commas that are outside quotes.
    private static List<string> Cells(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static ScreeningLog Full() => new()
    {
        PersonDetector = "yolo", PersonThreshold = 0.25, PersonMaxScore = 0.91234567, PersonSampled = 8, PersonKept = 3,
        PersonSamples = [new PersonSample(0.5, 0.9), new PersonSample(1.0, 0.1)],
        SuspiciousScore = 0.5555, SuspiciousThreshold = 0.5, SuspiciousModelAnswer = "no", SuspiciousDecision = "yes",
        ScoreSource = "logprobs",
        Agent = new AgentLog { Action = "send_whatsapp", Urgency = "high", Summary = "s", Reason = "r", FallbackUsed = false }
    };

    // ---- CSV ----

    [Fact]
    public void Every_csv_row_has_exactly_as_many_cells_as_the_header()
    {
        var lines = Lines(Job(null), Job(Full()), Job(new ScreeningLog()));
        var width = Cells(lines[0]).Count;
        Assert.Equal(31, width);
        for (var i = 1; i < lines.Length; i++) Assert.Equal(width, Cells(lines[i]).Count);
    }

    [Fact]
    public void Screening_values_land_under_their_own_headers()
    {
        var lines = Lines(Job(Full()));
        var row = Cells(lines[0]).Zip(Cells(lines[1])).ToDictionary(p => p.First, p => p.Second);
        Assert.Equal("yolo", row["person_detector"]);
        Assert.Equal("0.25", row["person_threshold"]);
        Assert.Equal("0.9123", row["person_max_score"]);          // four decimals
        Assert.Equal("0.5555", row["suspicious_score"]);
        Assert.Equal("0.5", row["suspicious_threshold"]);
        Assert.Equal("no", row["suspicious_model_answer"]);
        Assert.Equal("logprobs", row["score_source"]);
        Assert.Equal("send_whatsapp", row["agent_action"]);
        Assert.Equal("high", row["agent_urgency"]);
        Assert.Equal("no", row["agent_fallback"]);
    }

    [Fact]
    public void A_job_without_screening_leaves_every_screening_cell_empty()
    {
        var lines = Lines(Job(null));
        var row = Cells(lines[0]).Zip(Cells(lines[1])).ToDictionary(p => p.First, p => p.Second);
        foreach (var column in new[] { "person_detector", "person_threshold", "person_max_score", "suspicious_score",
                     "suspicious_threshold", "suspicious_model_answer", "score_source", "agent_action", "agent_urgency", "agent_fallback" })
            Assert.Equal("", row[column]);
    }

    [Fact]
    public void A_fallback_is_marked_yes_and_a_missing_agent_log_is_left_blank_not_no()
    {
        var fallback = Full() with { Agent = new AgentLog { Action = "none", FallbackUsed = true, Error = "timeout" } };
        var lines = Lines(Job(fallback), Job(Full() with { Agent = null }));
        var header = Cells(lines[0]);
        var at = header.IndexOf("agent_fallback");
        Assert.Equal("yes", Cells(lines[1])[at]);
        Assert.Equal("", Cells(lines[2])[at]);
    }

    [Fact]
    public void Scores_are_written_with_a_dot_regardless_of_the_machines_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("id-ID");
            var lines = Lines(Job(Full()));
            Assert.Contains("0.5555", lines[1]);
            Assert.DoesNotContain("0,5555", lines[1]);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    // ---- JSON ----

    [Fact]
    public void The_json_export_carries_the_screening_log_with_scores_thresholds_and_agent_decision()
    {
        using var doc = JsonDocument.Parse(JobExporter.ToJson([Job(Full())], T0));
        var s = doc.RootElement.GetProperty("jobs")[0].GetProperty("screening");
        Assert.Equal("yolo", s.GetProperty("personDetector").GetString());
        Assert.Equal(0.25, s.GetProperty("personThreshold").GetDouble());
        Assert.Equal(8, s.GetProperty("personSampled").GetInt32());
        Assert.Equal(3, s.GetProperty("personKept").GetInt32());
        Assert.Equal(2, s.GetProperty("personSamples").GetArrayLength());
        Assert.Equal(0.5555, s.GetProperty("suspiciousScore").GetDouble());
        Assert.Equal(0.5, s.GetProperty("suspiciousThreshold").GetDouble());
        Assert.Equal("no", s.GetProperty("suspiciousModelAnswer").GetString());
        Assert.Equal("yes", s.GetProperty("suspiciousDecision").GetString());
        Assert.Equal("logprobs", s.GetProperty("scoreSource").GetString());
        Assert.Equal("send_whatsapp", s.GetProperty("agent").GetProperty("action").GetString());
    }

    [Fact]
    public void The_json_export_has_a_null_screening_for_jobs_that_predate_it()
    {
        using var doc = JsonDocument.Parse(JobExporter.ToJson([Job(null)], T0));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("jobs")[0].GetProperty("screening").ValueKind);
    }
}

public class ScreeningPersistenceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void New_settings_default_to_yolo_a_quarter_person_threshold_and_a_half_flag_threshold()
    {
        var s = new ModelSettings();
        Assert.Equal("yolo", s.PersonDetector);
        Assert.Equal(0.25, s.PersonThreshold);
        Assert.Equal(0.5, s.SuspiciousThreshold);
        Assert.True(s.AgentEnabled);
    }

    [Fact]
    public void Settings_saved_before_these_features_still_load_with_the_new_defaults()
    {
        var old = JsonSerializer.Deserialize<ModelSettings>(
            """{"mode":"local","endpoint":"http://127.0.0.1:11434/v1","model":"m","personLabels":true,"flagActivity":"x"}""", Web)!;
        Assert.Equal("yolo", old.PersonDetector);
        Assert.Equal(0.25, old.PersonThreshold);
        Assert.Equal(0.5, old.SuspiciousThreshold);
        Assert.True(old.AgentEnabled);
        Assert.Equal("x", old.FlagActivity);
    }

    [Fact]
    public void A_job_saved_before_screening_existed_loads_with_no_screening()
    {
        var old = JsonSerializer.Deserialize<VideoJob>("""{"id":"a","name":"a.mp4","status":"completed","suspicious":"yes"}""", Web)!;
        Assert.Null(old.Screening);
        Assert.Equal("yes", old.Suspicious);
    }

    [Fact]
    public void A_screening_log_survives_a_round_trip_through_the_state_file()
    {
        var log = new ScreeningLog
        {
            PersonDetector = "llm", PersonThreshold = 0.3, PersonMaxScore = 0.8, PersonSampled = 4, PersonKept = 2,
            PersonSamples = [new PersonSample(0.5, 0.8)], SuspiciousScore = 0.61, SuspiciousThreshold = 0.5,
            SuspiciousModelAnswer = "no", SuspiciousDecision = "yes", ScoreSource = "logprobs",
            Agent = new AgentLog { Action = "send_email", Urgency = "low", Summary = "s", Reason = "r", ExtraFramePasses = 1 }
        };
        var back = JsonSerializer.Deserialize<ScreeningLog>(JsonSerializer.Serialize(log, Web), Web)!;
        Assert.Equal(log.SuspiciousScore, back.SuspiciousScore);
        Assert.Equal("logprobs", back.ScoreSource);
        Assert.Equal(0.5, back.PersonSamples[0].T);
        Assert.Equal("send_email", back.Agent!.Action);
        Assert.Equal(1, back.Agent.ExtraFramePasses);
    }
}
