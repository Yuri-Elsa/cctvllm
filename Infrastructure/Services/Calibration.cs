using System.Globalization;
using System.Text;

// One row of the curve: what would have happened if the "suspicious" threshold had been `Threshold`.
// A video is flagged when its p(yes) score is at least the threshold, the same rule Scoring.Decide applies.
public sealed record CalibrationPoint(double Threshold, int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative,
    double? Precision, double? Recall, double? F1, double? FalseAlarmRate);

// A threshold worth considering, and why.
public sealed record CalibrationPick(string Key, string Label, string Why, CalibrationPoint Point);

public sealed record CalibrationReport(
    int Considered,          // completed videos in the chosen camera(s)
    int Used,                // videos that went into the curve
    int Positives,           // real events among them (confirmed + missed)
    int Negatives,           // normal videos among them (false alarm + normal + assumed normal)
    int AssumedNormal,       // negatives that nobody looked at: unflagged and unreviewed
    int CheckedUnflagged,    // unflagged videos a person marked Missed or Normal
    int PendingReview,       // flagged but not reviewed yet (left out)
    int NoScore,             // no p(yes) score, only the model's yes/no (left out)
    int NoPeople,            // nobody detected, never reached the model (left out)
    int WeaponFlagged,       // flagged for a weapon; that flag has no threshold (left out)
    double CurrentThreshold,
    CalibrationPoint? Current,
    IReadOnlyList<CalibrationPoint> Curve,
    IReadOnlyList<CalibrationPick> Picks,
    IReadOnlyList<string> Warnings);

// Turns the reviewers' verdicts into a precision / recall curve for the suspicious threshold.
//   real event:  Confirmed (flagged, and right), Missed (not flagged, but it was an event)
//   normal:      False alarm (flagged, but nothing happened), Normal (not flagged, and nothing happened)
//   not reviewed: a flagged video is left out (we do not know yet); an unflagged one counts as normal
//                 when `assumeUnreviewedNormal` is on, which is how most footage should be treated but is an assumption.
public static class Calibration
{
    public const int MinPositives = 10;
    public const int MinNegatives = 30;

    public static CalibrationReport Compute(IEnumerable<VideoJob> jobs, string? camera, bool assumeUnreviewedNormal, double currentThreshold)
    {
        var scored = new List<(double Score, bool Positive)>();
        int considered = 0, noScore = 0, noPeople = 0, weapon = 0, pending = 0, assumed = 0, checkedUnflagged = 0;
        var models = new HashSet<string>();

        foreach (var job in JobExporter.Select(jobs, camera, false).Where(j => j.Status == "completed"))
        {
            considered++;
            if (job.NoPeople) { noPeople++; continue; }
            if (job.Weapon == "yes") { weapon++; continue; }
            if (job.Screening?.SuspiciousScore is not { } score || double.IsNaN(score)) { noScore++; continue; }

            bool? positive = job.ReviewState switch
            {
                "confirmed" or "missed" => true,
                "false_alarm" or "normal" => false,
                _ => null
            };
            if (positive is null)
            {
                if (job.Suspicious != "yes" && assumeUnreviewedNormal) { positive = false; assumed++; }
                else { pending++; continue; }
            }
            if (job.ReviewState is "missed" or "normal") checkedUnflagged++;
            scored.Add((score, positive.Value));
            if (!string.IsNullOrEmpty(job.Model)) models.Add(job.Model);
        }

        var positives = scored.Count(x => x.Positive);
        var negatives = scored.Count - positives;
        var curve = Enumerable.Range(0, 101).Select(i => Evaluate(scored, positives, negatives, i / 100.0)).ToList();
        var current = scored.Count > 0 ? Evaluate(scored, positives, negatives, currentThreshold) : null;

        var warnings = new List<string>();
        if (scored.Count == 0)
            warnings.Add("Nothing to calibrate yet: no video has both a probability score and a review verdict.");
        else
        {
            if (positives < MinPositives)
                warnings.Add($"Only {positives} real event(s) so far. The curve is a rough guide; aim for {MinPositives} or more, ideally 30.");
            if (negatives < MinNegatives)
                warnings.Add($"Only {negatives} normal video(s) so far. Aim for {MinNegatives} or more.");
            if (checkedUnflagged == 0)
                warnings.Add("No unflagged video has been checked, so recall assumes that nothing below the threshold was a real event. " +
                    "Open some unflagged videos and press Missed event or Normal.");
            if (negatives == 0 || positives == 0)
                warnings.Add("The curve needs both real events and normal videos before the recommendations mean anything.");
        }
        if (noScore > 0)
            warnings.Add($"{noScore} video(s) have no probability score (only the model's yes/no) and are not used. Scores need an Ollama version that returns token probabilities.");
        if (models.Count > 1)
            warnings.Add($"The scores come from {models.Count} different models ({string.Join(", ", models.Order())}). A threshold only fits the model that produced the scores.");

        return new CalibrationReport(considered, scored.Count, positives, negatives, assumed, checkedUnflagged, pending, noScore, noPeople, weapon,
            currentThreshold, current, curve, Picks(curve, positives, negatives), warnings);
    }

    private static CalibrationPoint Evaluate(List<(double Score, bool Positive)> scored, int positives, int negatives, double threshold)
    {
        int tp = 0, fp = 0;
        foreach (var (score, positive) in scored)
            if (score >= threshold) { if (positive) tp++; else fp++; }
        var fn = positives - tp;
        var tn = negatives - fp;
        var denominator = 2 * tp + fp + fn;
        return new CalibrationPoint(threshold, tp, fp, fn, tn,
            tp + fp > 0 ? (double)tp / (tp + fp) : null,
            positives > 0 ? (double)tp / positives : null,
            denominator > 0 ? 2.0 * tp / denominator : null,
            negatives > 0 ? (double)fp / negatives : null);
    }

    private static List<CalibrationPick> Picks(List<CalibrationPoint> curve, int positives, int negatives)
    {
        var picks = new List<CalibrationPick>();
        if (positives == 0 || negatives == 0) return picks;

        // Balanced: the best F1. On a plateau of equal values take the middle, which is the least fragile choice.
        var best = curve.Max(p => p.F1 ?? -1);
        var plateau = curve.Where(p => p.F1 is { } f && f >= best - 1e-9).ToList();
        picks.Add(new CalibrationPick("balanced", "Best balance",
            "The highest F1: the best compromise between missed events and false alarms.", plateau[(plateau.Count - 1) / 2]));

        // Safety first: the highest threshold that still catches (nearly) every event.
        var catching = curve.LastOrDefault(p => p.Recall is >= 0.95);
        if (catching is not null)
            picks.Add(new CalibrationPick("recall", "Catch almost everything",
                "The highest threshold that still finds at least 95% of the real events. Expect more false alarms.", catching));

        // Quiet: the lowest threshold whose alerts are almost all right.
        var quiet = curve.FirstOrDefault(p => p.Precision is >= 0.90 && p.TruePositive > 0);
        if (quiet is not null)
            picks.Add(new CalibrationPick("precision", "Few false alarms",
                "The lowest threshold at which at least 90% of the alerts are real. Expect more missed events.", quiet));
        return picks;
    }

    public static byte[] ToCsv(CalibrationReport report)
    {
        string Num(double? v) => v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder("threshold,true_positive,false_positive,false_negative,true_negative,precision,recall,f1,false_alarm_rate\r\n");
        foreach (var p in report.Curve)
            sb.Append(string.Join(',', Num(p.Threshold), p.TruePositive, p.FalsePositive, p.FalseNegative, p.TrueNegative,
                Num(p.Precision), Num(p.Recall), Num(p.F1), Num(p.FalseAlarmRate))).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
