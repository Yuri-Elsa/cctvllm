public sealed record NotificationPayload(
    string JobId, string CameraName, DateTimeOffset Time, string Description,
    bool Suspicious, bool Weapon, string ReviewUrl, int MergedCount = 0)
{
    public const string Disclaimer = "Penandaan otomatis oleh model, bukan kesimpulan. Wajib ditinjau manusia.";
}