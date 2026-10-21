using System.Net.Mail;
using Microsoft.Extensions.Options;

public sealed class LogNotifier(ILogger<LogNotifier> logger) : INotifier
{
    public string Name => "log";
    public bool Enabled => true;
    public Task SendAsync(NotificationPayload p, CancellationToken ct)
    {
        if (p.IsModelFlag)
            logger.LogWarning("Flagged video {Job} camera={Camera} suspicious={Suspicious} weapon={Weapon} merged={Merged}",
                p.JobId, p.CameraName, p.Suspicious, p.Weapon, p.MergedCount);
        else
            logger.LogWarning("Alert {Kind} camera={Camera} merged={Merged}: {Description}", p.Kind, p.CameraName, p.MergedCount, p.Description);
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
            kind = p.Kind,
            jobId = p.JobId,
            camera = p.CameraName,
            time = p.Time,
            description = p.Description,
            flags = new { suspicious = p.Suspicious, weapon = p.Weapon },
            urgency = p.Urgency,
            summary = p.Summary,
            reviewUrl = p.ReviewUrl,
            mergedCount = p.MergedCount,
            notice = p.IsModelFlag ? NotificationPayload.Disclaimer : null
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
        var agent = string.IsNullOrWhiteSpace(p.Summary) ? "" : $"Urgensi: {p.Urgency}\nRingkasan: {p.Summary}\n";
        var body = p.IsModelFlag
            ? $"{NotificationPayload.Disclaimer}\n\nKamera: {p.CameraName}\nWaktu: {p.Time:u}\n{agent}" +
              $"Suspicious: {p.Suspicious}   Weapon: {p.Weapon}\nDeskripsi: {p.Description}\nTinjau: {p.ReviewUrl}\n"
            : $"Kamera: {p.CameraName}\nWaktu: {p.Time:u}\nKeterangan: {p.Description}\nTinjau: {p.ReviewUrl}\n";
        using var message = new MailMessage(o.MailFrom ?? o.SmtpUser ?? "cctv@localhost", o.MailTo!)
        {
            Subject = p.Subject(),
            Body = body
        };
        await client.SendMailAsync(message, ct);
    }
}