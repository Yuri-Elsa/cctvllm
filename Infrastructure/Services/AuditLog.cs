using System.Text.Json;

public sealed record AuditEntry(DateTimeOffset Time, string Actor, string? Address, string Action, string? Target, string? Detail);

public sealed class AuditLog(string root)
{
    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);
    private const long MaxBytes = 10 * 1024 * 1024;
    private readonly string path = Path.Combine(root, "audit.jsonl");
    private readonly object gate = new();

    public void Record(string actor, string? address, string action, string? target = null, string? detail = null)
    {
        var line = JsonSerializer.Serialize(new AuditEntry(DateTimeOffset.UtcNow, Cut(actor)!, Cut(address), action, Cut(target), Cut(detail)), Compact);
        lock (gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.Combine(root, $"audit-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
                File.AppendAllText(path, line + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"audit.jsonl could not be written ({ex.Message}): {line}");
            }
        }
    }

    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        lock (gate)
        {
            if (!File.Exists(path)) return [];
            var entries = new List<AuditEntry>();
            foreach (var line in File.ReadLines(path).Reverse().Take(limit))
            {
                try { if (JsonSerializer.Deserialize<AuditEntry>(line, Compact) is { } entry) entries.Add(entry); }
                catch (JsonException) { }
            }
            return entries;
        }
    }

    private static string? Cut(string? text) => text is { Length: > 500 } ? text[..500] + "…" : text;
}