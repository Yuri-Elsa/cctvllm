using System.Net.Mail;
using Microsoft.Extensions.Options;

public sealed class LogNotifier(ILogger<LogNotifier> logger) : INotifier
{
    public string Name => "log";
    public bool Enabled => true; 
    public Task SendAsync(NotificationPayload p, CancellationToken ct)
    {
        logger.LogWarning("Flagged video {Job} camera={Camera} suspicious={Suspicious} weapon={Weapon} merged={Merged}",
            p.JobId, p.CameraName, p.Suspicious, p.Weapon, p.MergedCount);
        return Task.CompletedTask;
    }
}

public sealed class WebhookNotifier(HttpClient http, IOptions<NotificationOptions> options) : INotifier
{
    public string Name => "webhook";
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.WebhookUrl);
    public async Task SendAsync(NotificationPayload p, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(options.Value.WebhookUrl, new
        {
            jobId = p.JobId,
            camera = p.CameraName,
            time = p.Time,
            description = p.Description,
            flags = new { suspicious = p.Suspicious, weapon = p.Weapon },
            reviewUrl = p.ReviewUrl,
            mergedCount = p.MergedCount,
            notice = NotificationPayload.Disclaimer
        }, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class EmailNotifier(IOptions<NotificationOptions> options) : INotifier
{
    public string Name => "email";
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.SmtpHost) && !string.IsNullOrWhiteSpace(options.Value.MailTo);
    public async Task SendAsync(NotificationPayload p, CancellationToken ct)
    {
        var o = options.Value;
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = true };
        if (!string.IsNullOrEmpty(o.SmtpUser)) client.Credentials = new System.Net.NetworkCredential(o.SmtpUser, o.SmtpPassword);
        var merged = p.MergedCount > 0 ? $" (+{p.MergedCount} digabung)" : "";
        using var message = new MailMessage(o.MailFrom ?? o.SmtpUser ?? "cctv@localhost", o.MailTo!)
        {
            Subject = $"[CCTV] Penandaan otomatis: {p.CameraName}{merged}",
            Body = $"{NotificationPayload.Disclaimer}\n\nKamera: {p.CameraName}\nWaktu: {p.Time:u}\n" +
                   $"Suspicious: {p.Suspicious}   Weapon: {p.Weapon}\nDeskripsi: {p.Description}\nTinjau: {p.ReviewUrl}\n"
        };
        await client.SendMailAsync(message, ct);
    }
}