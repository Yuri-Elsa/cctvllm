using System.Security.Cryptography;
using System.Text;

public sealed class AuthOptions
{
    public string? Password => Environment.GetEnvironmentVariable("CCTV_UI_PASSWORD") is { Length: > 0 } p ? p : null;

    public bool Enabled => Password is not null;

    public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cctv-ui:" + Password)))[..16];

    public bool Check(string? attempt)
    {
        if (Password is null || attempt is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(attempt)), SHA256.HashData(Encoding.UTF8.GetBytes(Password)));
    }

    public static bool IsLoopbackOnly(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return true;
        foreach (var raw in urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rest = raw;
            var scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) rest = rest[(scheme + 3)..];
            rest = rest.Split('/')[0];
            var host = rest.StartsWith('[') ? rest[..(rest.IndexOf(']') + 1)]
                : rest.Contains(':') ? rest[..rest.LastIndexOf(':')] : rest;
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "[::1]" || host.StartsWith("127.");
            if (!local) return false;
        }
        return true;
    }
}

public sealed class LoginThrottle(int maxFailures = 5, TimeSpan? window = null)
{
    private readonly TimeSpan span = window ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (int Count, DateTimeOffset Since)> failures = new();
    private readonly object gate = new();

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public bool IsBlocked(string key)
    {
        lock (gate)
        {
            if (!failures.TryGetValue(key, out var entry)) return false;
            if (Now() - entry.Since >= span) { failures.Remove(key); return false; }
            return entry.Count >= maxFailures;
        }
    }

    public void Fail(string key)
    {
        lock (gate)
        {
            var now = Now();
            if (failures.Count > 1000)
                foreach (var old in failures.Where(kv => now - kv.Value.Since >= span).Select(kv => kv.Key).ToList()) failures.Remove(old);
            failures[key] = failures.TryGetValue(key, out var entry) && now - entry.Since < span ? (entry.Count + 1, entry.Since) : (1, now);
        }
    }

    public void Reset(string key) { lock (gate) failures.Remove(key); }
}