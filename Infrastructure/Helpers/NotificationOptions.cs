public sealed class NotificationOptions
{
    public int MaxAttempts { get; set; } = 3;
    // One send attempt (one channel) may take at most this long, so a hung SMTP or webhook server cannot hold up the rest.
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);
    // A channel that still fails after MaxAttempts is tried again later (30 s, 1 min, 2 min ... up to RetryMax)
    // until RetryWindow has passed since the alert was raised. CCTV_ALERT_RETRY_HOURS changes the window.
    public TimeSpan RetryBase { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryMax { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromHours(
        double.TryParse(Environment.GetEnvironmentVariable("CCTV_ALERT_RETRY_HOURS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var retryHours) && retryHours > 0 ? retryHours : 6);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ThrottleWindow { get; set; } = TimeSpan.FromMinutes(5);   
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);  
    public int QueueCapacity { get; set; } = 1000;
    public int MaxDescriptionChars { get; set; } = 600;
    public string ReviewBaseUrl { get; set; } = Environment.GetEnvironmentVariable("CCTV_REVIEW_URL")
        ?? Environment.GetEnvironmentVariable("CCTV_URL") ?? "http://127.0.0.1:8766";
    public string? WebhookUrl => Environment.GetEnvironmentVariable("CCTV_WEBHOOK_URL");
    public string? SmtpHost => Environment.GetEnvironmentVariable("CCTV_SMTP_HOST");
    public int SmtpPort => int.TryParse(Environment.GetEnvironmentVariable("CCTV_SMTP_PORT"), out var port) ? port : 587;
    public string? SmtpUser => Environment.GetEnvironmentVariable("CCTV_SMTP_USER");
    public string? SmtpPassword => Environment.GetEnvironmentVariable("CCTV_SMTP_PASSWORD");
    public string? MailFrom => Environment.GetEnvironmentVariable("CCTV_MAIL_FROM");
    public string? MailTo => Environment.GetEnvironmentVariable("CCTV_MAIL_TO");
}