using System.Text;
using System.Text.Json;
using Xunit;

public class JobExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static VideoJob Job(string id, string? camera, int minute, Func<VideoJob, VideoJob>? change = null)
    {
        var job = new VideoJob
        {
            Id = id,
            Name = id + ".mp4",
            CameraName = camera,
            Status = "completed",
            Stage = "Done",
            Duration = 60,
            SegmentStart = T0.AddMinutes(minute),
            Created = T0.AddMinutes(minute),
            Suspicious = "no",
            Weapon = "no",
            Description = "Tidak ada kejadian."
        };
        return change is null ? job : change(job);
    }

    private static VideoJob FlaggedJob(string id, string? camera, int minute) =>
        Job(id, camera, minute, j => j with { Suspicious = "yes" });

    // ---- selection ----

    [Fact]
    public void Select_filters_by_camera_and_keeps_chronological_order()
    {
        var jobs = new[] { Job("b", "lobby", 5), Job("a", "lobby", 1), Job("c", "gudang", 3), Job("d", null, 2) };
        Assert.Equal(new[] { "a", "b" }, JobExporter.Select(jobs, "lobby", false).Select(j => j.Id).ToArray());
        Assert.Equal(new[] { "d" }, JobExporter.Select(jobs, "::none", false).Select(j => j.Id).ToArray());
        Assert.Equal(new[] { "a", "d", "c", "b" }, JobExporter.Select(jobs, "all", false).Select(j => j.Id).ToArray());
        Assert.Equal(4, JobExporter.Select(jobs, null, false).Count());
    }

    [Fact]
    public void Flagged_only_keeps_finished_videos_the_model_flagged()
    {
        var jobs = new[]
        {
            FlaggedJob("flagged", "lobby", 1),
            Job("clean", "lobby", 2),
            FlaggedJob("failed", "lobby", 3) with { Status = "failed" },   // a failed job is never "flagged", as in the UI
            Job("weapon", "lobby", 4, j => j with { Weapon = "yes" })
        };
        Assert.Equal(new[] { "flagged", "weapon" }, JobExporter.Select(jobs, null, true).Select(j => j.Id).ToArray());
    }

    // ---- CSV ----

    [Fact]
    public void Csv_starts_with_a_utf8_byte_order_mark_and_a_header_row()
    {
        var bytes = JobExporter.ToCsv([]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("id,camera,segment_start,segment_end,file,duration_s,status,stage,suspicious,weapon,flagged,review,description,", text);
        Assert.EndsWith("\r\n", text);
    }

    [Fact]
    public void Csv_has_one_row_per_job_with_review_flag_and_times()
    {
        var job = FlaggedJob("a", "lobby", 0) with { ReviewState = "false_alarm", NotifiedAt = T0.AddMinutes(2), NotifyError = "email: timeout" };
        var lines = Encoding.UTF8.GetString(JobExporter.ToCsv([job])[3..]).TrimEnd().Split("\r\n");
        Assert.Equal(2, lines.Length);
        var row = lines[1];
        Assert.StartsWith("a,lobby,2026-10-15T08:00:00.0000000+00:00,2026-10-15T08:01:00.0000000+00:00,a.mp4,60,completed,Done,yes,no,yes,false_alarm,", row);
        Assert.Contains("2026-10-15T08:02:00.0000000+00:00,email: timeout", row);
    }

    [Fact]
    public void Csv_cells_are_quoted_when_they_hold_commas_quotes_or_line_breaks()
    {
        Assert.Equal("plain", JobExporter.Cell("plain"));
        Assert.Equal("\"a,b\"", JobExporter.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", JobExporter.Cell("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", JobExporter.Cell("line1\nline2"));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+62812", "'+62812")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Csv_cells_that_start_like_a_formula_are_neutralised(string input, string expected) =>
        Assert.Equal(expected, JobExporter.Cell(input));

    [Fact]
    public void A_model_description_cannot_turn_into_a_formula_in_the_export()
    {
        var job = Job("a", "lobby", 0, j => j with { Description = "=HYPERLINK(\"http://evil\",\"x\")" });
        var text = Encoding.UTF8.GetString(JobExporter.ToCsv([job])[3..]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"", text);
    }

    // ---- JSON ----

    [Fact]
    public void Json_reports_the_count_and_each_job()
    {
        var jobs = new[] { FlaggedJob("a", "lobby", 0) with { ReviewState = "confirmed" }, Job("b", null, 1) };
        using var doc = JsonDocument.Parse(JobExporter.ToJson(jobs, T0));
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var first = doc.RootElement.GetProperty("jobs")[0];
        Assert.Equal("a", first.GetProperty("id").GetString());
        Assert.Equal("lobby", first.GetProperty("camera").GetString());
        Assert.True(first.GetProperty("flagged").GetBoolean());
        Assert.Equal("confirmed", first.GetProperty("review").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("jobs")[1].GetProperty("camera").ValueKind);
    }

    [Fact]
    public void A_disabled_weapon_check_is_exported_as_disabled_not_as_no()
    {
        var job = Job("a", "lobby", 0, j => j with { WeaponEnabled = false, Weapon = "no" });
        using var doc = JsonDocument.Parse(JobExporter.ToJson([job], T0));
        Assert.Equal("disabled", doc.RootElement.GetProperty("jobs")[0].GetProperty("weapon").GetString());
        Assert.Contains(",disabled,", Encoding.UTF8.GetString(JobExporter.ToCsv([job])[3..]));
    }
}