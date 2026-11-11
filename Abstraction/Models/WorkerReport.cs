// What a remote worker sends the coordinator when it has finished a video.
public sealed class WorkerReport
{
    public DescriptionResult Result { get; set; } = null!;
    public bool BlurRequested { get; set; }
    public bool BlurDone { get; set; }
    public string? BlurError { get; set; }
    public double ElapsedSeconds { get; set; }
}

// What the coordinator hands a worker that asked for work.
public sealed record WorkerClaim(VideoJob Job, ModelSettings Settings, int LeaseSeconds);
