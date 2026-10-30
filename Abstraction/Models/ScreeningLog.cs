public sealed record PersonSample(double T, double Score);

public sealed record AgentLog
{
    public string Action { get; init; } = "";
    public string? Urgency { get; init; }
    public string? Summary { get; init; }
    public string? Reason { get; init; }
    public bool FallbackUsed { get; init; }
    public string? Error { get; init; }
    public bool GuardOverride { get; init; }
    public int ExtraFramePasses { get; init; }
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ScreeningLog
{
    public string? PersonDetector { get; init; }
    public double? PersonThreshold { get; init; }
    public double? PersonMaxScore { get; init; }
    public int PersonSampled { get; init; }
    public int PersonKept { get; init; }
    public int PersonLlmChecked { get; init; }    
    public int PersonLlmRejected { get; init; }    
    public PersonSample[] PersonSamples { get; init; } = [];

    public double? SuspiciousScore { get; init; }
    public double? SuspiciousThreshold { get; init; }
    public string? SuspiciousModelAnswer { get; init; }
    public string? SuspiciousDecision { get; init; }
    public string ScoreSource { get; init; } = "none";

    public AgentLog? Agent { get; init; }
}