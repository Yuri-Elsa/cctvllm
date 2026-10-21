using System.Globalization;
using System.Text;
using System.Text.Json;

public static class JobExporter
{
    private static readonly string[] Columns =
    [
        "id", "camera", "segment_start", "segment_end", "file", "duration_s", "status", "stage",
        "suspicious", "weapon", "flagged", "review", "description", "error",
        "notified_at", "notify_error", "model", "backend", "elapsed_s", "no_people", "created",
        "person_detector", "person_threshold", "person_max_score", "suspicious_score", "suspicious_threshold",
        "suspicious_model_answer", "score_source", "agent_action", "agent_urgency", "agent_fallback"
    ];

    public static bool IsFlagged(VideoJob j) => j.Status == "completed" && (j.Suspicious == "yes" || j.Weapon == "yes");

    public static IEnumerable<VideoJob> Select(IEnumerable<VideoJob> jobs, string? camera, bool flaggedOnly)
    {
        if (!string.IsNullOrEmpty(camera) && camera != "all")
            jobs = camera == "::none" ? jobs.Where(j => string.IsNullOrEmpty(j.CameraName))
                : jobs.Where(j => string.Equals(j.CameraName, camera, StringComparison.Ordinal));
        if (flaggedOnly) jobs = jobs.Where(IsFlagged);
        return jobs.OrderBy(j => j.SegmentStart ?? j.Created).ThenBy(j => j.Created);
    }

    private static string Iso(DateTimeOffset? t) => t?.ToString("o", CultureInfo.InvariantCulture) ?? "";
    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Score(double? v) => v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private static string? WeaponOf(VideoJob j) => j.WeaponEnabled ? j.Weapon : "disabled";

    private static string[] Row(VideoJob j) =>
    [
        j.Id, j.CameraName ?? "", Iso(j.SegmentStart), j.SegmentStart is { } s ? Iso(s.AddSeconds(j.Duration)) : "",
        j.Name, Num(j.Duration), j.Status, j.Stage,
        j.Suspicious ?? "", WeaponOf(j) ?? "", IsFlagged(j) ? "yes" : "no", j.ReviewState ?? "",
        j.Description ?? "", j.Error ?? "",
        Iso(j.NotifiedAt), j.NotifyError ?? "", j.Model ?? "", j.BackendMode ?? "",
        j.Elapsed is { } e ? Num(e) : "", j.NoPeople ? "yes" : "no", Iso(j.Created),
        j.Screening?.PersonDetector ?? "", Score(j.Screening?.PersonThreshold), Score(j.Screening?.PersonMaxScore),
        Score(j.Screening?.SuspiciousScore), Score(j.Screening?.SuspiciousThreshold),
        j.Screening?.SuspiciousModelAnswer ?? "", j.Screening?.ScoreSource ?? "",
        j.Screening?.Agent?.Action ?? "", j.Screening?.Agent?.Urgency ?? "",
        j.Screening?.Agent is { } agent ? (agent.FallbackUsed ? "yes" : "no") : ""
    ];

    public static byte[] ToCsv(IEnumerable<VideoJob> jobs)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var job in jobs) sb.Append(string.Join(',', Row(job).Select(Cell))).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static byte[] ToJson(IEnumerable<VideoJob> jobs, DateTimeOffset now)
    {
        var rows = jobs.Select(j => new
        {
            id = j.Id,
            camera = j.CameraName,
            segmentStart = j.SegmentStart,
            segmentEnd = j.SegmentStart?.AddSeconds(j.Duration),
            file = j.Name,
            durationSeconds = j.Duration,
            status = j.Status,
            stage = j.Stage,
            suspicious = j.Suspicious,
            weapon = WeaponOf(j),
            flagged = IsFlagged(j),
            review = j.ReviewState,
            description = j.Description,
            error = j.Error,
            notifiedAt = j.NotifiedAt,
            notifyError = j.NotifyError,
            model = j.Model,
            backend = j.BackendMode,
            elapsedSeconds = j.Elapsed,
            noPeople = j.NoPeople,
            created = j.Created,
            screening = j.Screening
        }).ToList();
        return JsonSerializer.SerializeToUtf8Bytes(new { exportedAt = now, count = rows.Count, jobs = rows },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}