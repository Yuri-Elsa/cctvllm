using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

public sealed class NotifierException(string message) : Exception(message);

public sealed partial class WhatsAppNotifier(HttpClient http, IOptions<WhatsAppOptions> options) : INotifier
{
    public string Name => "whatsapp";
    public bool Enabled => options.Value.Configured;

    private readonly HashSet<string> served = new();
    private readonly object gate = new();

    public async Task SendAsync(NotificationPayload p, CancellationToken ct)
    {
        var o = options.Value;
        var failed = new List<string>();
        foreach (var to in o.Recipients)
        {
            var key = $"{p.Kind}|{p.JobId}|{p.CameraName}|{p.Time.UtcTicks}|{p.MergedCount}|{to}";
            lock (gate) { if (served.Contains(key)) continue; }
            try
            {
                if (o.Provider == "fonnte") await SendFonnte(o, to, p, ct); else await SendCloud(o, to, p, ct);
                lock (gate) { if (served.Count > 1000) served.Clear(); served.Add(key); }
            }
            catch (NotifierException ex) { failed.Add(ex.Message); }
            catch (HttpRequestException) { failed.Add("cannot reach the WhatsApp server"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { failed.Add("the WhatsApp server timed out"); }
        }
        if (failed.Count > 0)
            throw new NotifierException($"{failed.Count} of {o.Recipients.Count} recipients failed: {string.Join("; ", failed.Distinct())}");
    }

    private async Task SendCloud(WhatsAppOptions o, string to, NotificationPayload p, CancellationToken ct)
    {
        if (to.Contains('@')) throw new NotifierException("the Cloud API cannot send to groups");
        object body = !string.IsNullOrWhiteSpace(o.Template)
            ? new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = o.Template,
                    language = new { code = o.TemplateLanguage },
                    components = new[]
                    {
                        new
                        {
                            type = "body",
                            parameters = new[]
                            {
                                new { type = "text", text = Param(p.CameraName, 80) },
                                new { type = "text", text = Param(p.Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), 40) },
                                new { type = "text", text = Param(Summary(p), 400) }
                            }
                        }
                    }
                }
            }
            : new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { preview_url = false, body = Text(p) }
            };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{o.ApiVersion}/{o.PhoneId}/messages")
        { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        string? message = null; var code = 0;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n)) code = n;
                if (error.TryGetProperty("message", out var m)) message = m.GetString();
            }
        }
        catch (JsonException) { }
        var detail = message is null ? $"HTTP {(int)response.StatusCode}" : $"{(code != 0 ? code + ": " : "")}{Cut(message, 200)}";
        var hint = string.IsNullOrWhiteSpace(o.Template)
            ? " (no template set: free text only reaches a recipient who wrote to this number in the last 24 hours; set CCTV_WA_TEMPLATE)" : "";
        throw new NotifierException($"Meta rejected the message: {detail}{hint}");
    }

    private async Task SendFonnte(WhatsAppOptions o, string to, NotificationPayload p, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fonnte.com/send")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["target"] = to, ["message"] = Text(p) })
        };
        request.Headers.TryAddWithoutValidation("Authorization", o.Token);   // the raw token, no "Bearer"
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        var accepted = false; string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("status") || property.Name.Equals("status", StringComparison.OrdinalIgnoreCase))
                    accepted = property.Value.ValueKind == JsonValueKind.True
                        || (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()?.ToLowerInvariant() == "true");
                else if (property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    reason = property.Value.GetString();
            }
        }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode || !accepted)
            throw new NotifierException($"Fonnte rejected the message: {(reason is null ? $"HTTP {(int)response.StatusCode}" : Cut(reason, 120))}");
    }

    private static string Text(NotificationPayload p)
    {
        var sb = new StringBuilder();
        sb.AppendLine(p.Subject());
        sb.AppendLine($"Waktu: {p.Time.ToLocalTime():dd/MM/yyyy HH:mm:ss}");
        if (p.IsModelFlag) sb.AppendLine($"Suspicious: {(p.Suspicious ? "ya" : "tidak")} | Weapon: {(p.Weapon ? "ya" : "tidak")}");
        sb.AppendLine(Message(p));
        sb.AppendLine($"Tinjau: {p.ReviewUrl}");
        if (p.IsModelFlag) sb.Append(NotificationPayload.Disclaimer);
        return sb.ToString().TrimEnd();
    }

    private static string Summary(NotificationPayload p) =>
        p.IsModelFlag
            ? $"Suspicious: {(p.Suspicious ? "ya" : "tidak")}, Weapon: {(p.Weapon ? "ya" : "tidak")}. {Message(p)}"
            : p.Description;

    private static string Message(NotificationPayload p) => string.IsNullOrWhiteSpace(p.Summary) ? p.Description : p.Summary;

    private static string Param(string value, int max) => Cut(Whitespace().Replace(value, " ").Trim(), max) is { Length: > 0 } s ? s : "-";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}