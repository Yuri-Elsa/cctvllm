public sealed class WhatsAppOptions
{
    public string? Provider => Env("CCTV_WA_PROVIDER")?.ToLowerInvariant() switch { "cloud" => "cloud", "fonnte" => "fonnte", _ => null };
    public string? Token => Env("CCTV_WA_TOKEN");
    public string? PhoneId => Env("CCTV_WA_PHONE_ID");
    public string ApiVersion => Env("CCTV_WA_API_VERSION") ?? "v26.0";
    public string? Template => Env("CCTV_WA_TEMPLATE");
    public string TemplateLanguage => Env("CCTV_WA_TEMPLATE_LANG") ?? "id";
    public string CountryCode => new string((Env("CCTV_WA_COUNTRY") ?? "62").Where(char.IsDigit).ToArray()) is { Length: > 0 } c ? c : "62";

    public IReadOnlyList<string> Recipients =>
        (Env("CCTV_WA_TO") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).OfType<string>().Distinct().ToList();

    public bool Configured => Provider is not null && !string.IsNullOrWhiteSpace(Token) && Recipients.Count > 0
        && (Provider != "cloud" || !string.IsNullOrWhiteSpace(PhoneId));

    private string? Normalize(string raw)
    {
        if (raw.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return raw;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = CountryCode + digits[1..];
        return digits.Length >= 8 ? digits : null;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}