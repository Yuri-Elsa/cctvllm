public static class AlertKind
{
    public const string Flag = "flag";                         
    public const string CameraOffline = "camera_offline"; 
    public const string CameraRecovered = "camera_recovered"; 
    public const string JobFailed = "job_failed";             
}

public sealed record NotificationPayload(
    string JobId, string CameraName, DateTimeOffset Time, string Description,
    bool Suspicious, bool Weapon, string ReviewUrl, int MergedCount = 0, string Kind = AlertKind.Flag)
{
    public const string Disclaimer = "Penandaan otomatis oleh model, bukan kesimpulan. Wajib ditinjau manusia.";

    public bool IsModelFlag => Kind == AlertKind.Flag;

    public string Subject()
    {
        var merged = MergedCount > 0 ? $" (+{MergedCount} digabung)" : "";
        return Kind switch
        {
            AlertKind.CameraOffline => $"[CCTV] Kamera offline: {CameraName}",
            AlertKind.CameraRecovered => $"[CCTV] Kamera kembali online: {CameraName}",
            AlertKind.JobFailed => $"[CCTV] Analisis video gagal: {CameraName}{merged}",
            _ => $"[CCTV] Penandaan otomatis: {CameraName}{merged}"
        };
    }
}