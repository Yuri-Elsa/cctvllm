// What the analysis of one video produced. A remote worker sends this to the coordinator as JSON, so it is a plain public record.
public sealed record DescriptionResult(string Description, string? Suspicious, string? Weapon, string Raw, double[] Timestamps, string? ModelName = null, bool NoPeople = false,
    double? SuspiciousScore = null, string? ModelSuspicious = null, string? ScoreSource = null, ScreeningLog? Person = null,
    double? NarrativeScore = null, double? ClassifierScore = null);

// Face blur outcome of one run: whether blur was asked for, whether the blurred copy exists, and why not when it does not.
public readonly record struct BlurOutcome(bool Requested, bool Done, string? Error);
