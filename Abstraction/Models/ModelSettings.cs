public sealed record ModelSettings(string Mode = "local", string Endpoint = "http://127.0.0.1:11434/v1", string Model = "gemma4:e2b-it-qat", bool PersonLabels = true)
{
    public string FlagActivity { get; init; } = "";
    public bool SiteContextEnabled { get; init; }
    public string SiteContext { get; init; } = "";
    public bool WeaponEnabled { get; init; } = true;
    public bool BatchProcessing { get; init; }
    public string PersonDetector { get; init; } = "yolo";
    public double PersonThreshold { get; init; } = 0.25;
    public double SuspiciousThreshold { get; init; } = 0.5;
    public bool AgentEnabled { get; init; } = true;
}