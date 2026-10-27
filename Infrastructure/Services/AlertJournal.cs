using System.Text.Json;

// Alerts that were accepted but not yet delivered, kept on disk so that a restart, or a channel that is down
// for a while, does not lose them.
//   waiting  : accepted, not delivered yet (queued, or held back by the throttle for a digest)
//   retrying : every attempt on some channel failed; try that channel again at NextTry
// An entry is removed once its channels have delivered it, or when it is older than RetryWindow.
public sealed class AlertJournal
{
    public sealed record Entry
    {
        public string Key { get; init; } = "";
        public NotificationPayload Message { get; init; } = default!;
        public string[] CoveredJobIds { get; init; } = [];
        public int Attempts { get; init; }
        public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? NextTry { get; init; }          // null while waiting
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly string? path;
    private readonly NotificationOptions options;
    private readonly ILogger? logger;

    // path = null keeps the journal in memory only (tests, or when there is nowhere to write).
    public AlertJournal(string? path, NotificationOptions options, ILogger? logger = null)
    {
        this.path = path; this.options = options; this.logger = logger;
        Load();
    }

    // Alerts that were still waiting when the app last stopped. The dispatcher sends them again on start.
    public IReadOnlyList<NotificationPayload> Recovered { get; private set; } = [];

    public int Count { get { lock (gate) return entries.Count; } }

    public void Add(NotificationPayload payload)
    {
        lock (gate)
        {
            entries[payload.Key!] = new Entry
            {
                Key = payload.Key!, Message = payload, CoveredJobIds = JobIds([payload])
            };
            Save();
        }
    }

    public void Complete(IEnumerable<string?> keys)
    {
        lock (gate)
        {
            var removed = false;
            foreach (var key in keys) if (key is not null && entries.Remove(key)) removed = true;
            if (removed) Save();
        }
    }

    // Records that delivery to the channels named in `message` failed. Returns true when it will be tried again.
    // retryKey is the entry of an earlier retry (null for the first failure of an alert or a digest).
    public bool Fail(NotificationPayload message, IReadOnlyList<NotificationPayload> covered, string? retryKey)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var created = now; var attempts = 0;
            if (retryKey is not null && entries.Remove(retryKey, out var previous))
            {
                created = previous.Created; attempts = previous.Attempts;
            }
            foreach (var item in covered)
                if (item.Key is not null && entries.Remove(item.Key, out var waiting) && waiting.Created < created) created = waiting.Created;

            attempts++;
            if (now - created > options.RetryWindow) { Save(); return false; }
            var key = retryKey ?? Guid.NewGuid().ToString("N");
            entries[key] = new Entry
            {
                Key = key, Message = message, CoveredJobIds = JobIds(covered), Attempts = attempts,
                Created = created, NextTry = now + Backoff(attempts)
            };
            Save();
            return true;
        }
    }

    public List<Entry> DueRetries(DateTimeOffset now)
    {
        lock (gate) return entries.Values.Where(e => e.NextTry is { } at && at <= now).OrderBy(e => e.NextTry).ToList();
    }

    // Drops alerts that are too old to be worth sending any more.
    public int Expire(DateTimeOffset now)
    {
        lock (gate)
        {
            var old = entries.Values.Where(e => now - e.Created > options.RetryWindow).Select(e => e.Key).ToList();
            foreach (var key in old) entries.Remove(key);
            if (old.Count > 0) { Save(); logger?.LogWarning("{Count} alert(s) were dropped after {Window} without being delivered", old.Count, options.RetryWindow); }
            return old.Count;
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var delay = options.RetryBase * Math.Pow(2, Math.Min(attempt, 20) - 1);
        return delay > options.RetryMax ? options.RetryMax : delay;
    }

    private static string[] JobIds(IEnumerable<NotificationPayload> items) =>
        items.Select(i => i.JobId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();

    private void Load()
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? [];
            var now = DateTimeOffset.UtcNow;
            var waiting = new List<NotificationPayload>();
            foreach (var entry in saved)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Message is null) continue;
                if (now - entry.Created > options.RetryWindow) continue;     // too old to matter
                entries[entry.Key] = entry;
                if (entry.NextTry is null) waiting.Add(entry.Message with { Key = entry.Key });
            }
            Recovered = waiting;
            if (entries.Count != saved.Count) Save();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged journal must not stop the app from starting. Keep it for inspection and begin empty.
            logger?.LogError(ex, "The alert journal could not be read; starting without it");
            try { File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch (IOException) { }
        }
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries.Values.ToList(), Json));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "The alert journal could not be saved");
        }
    }
}
