using System.Collections.Concurrent;

// The model servers a run may use: the main endpoint plus any extra ones from Settings. Each request goes to the
// least busy server that is up, so two GPUs share the load, and a server that cannot be reached is skipped for a
// while. CCTV_MODEL_CONCURRENCY (default 0 = no limit) caps how many requests one server gets at a time; with several
// workers it keeps a single GPU from being buried under requests that would then time out.
public static class ModelEndpoints
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    public const int MaxEndpoints = 8;

    private static readonly ConcurrentDictionary<string, EndpointState> states = new(StringComparer.OrdinalIgnoreCase);
    private static long rotation;

    internal static int ConcurrencyLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("CCTV_MODEL_CONCURRENCY"), out var n) && n > 0 ? Math.Min(n, 64) : 0;

    private static string Clean(string value) => value.Trim().TrimEnd('/');

    // Main endpoint first, then the extras, without blanks or duplicates.
    public static IReadOnlyList<string> All(ModelSettings config) => Parse(config.Endpoint, config.ExtraEndpoints);

    public static IReadOnlyList<string> Parse(string? primary, string? extras)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) list.Add(Clean(primary));
        foreach (var part in (extras ?? "").Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(Clean(part));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static EndpointState State(string endpoint) => states.GetOrAdd(endpoint, _ => new EndpointState(ConcurrencyLimit));

    public static bool IsDown(string endpoint) => State(endpoint).DownUntil > DateTime.UtcNow.Ticks;
    public static void MarkDown(string endpoint) => State(endpoint).DownUntil = (DateTime.UtcNow + Cooldown).Ticks;
    public static void MarkUp(string endpoint) => State(endpoint).DownUntil = 0;
    public static int InFlight(string endpoint) => State(endpoint).InFlight;

    // Reserves a place on the best server that is not in `exclude`. The place counts as load at once, so requests that
    // arrive together spread out instead of all choosing the same server. Dispose the slot when the request is done.
    public static Slot? Reserve(ModelSettings config, ISet<string>? exclude = null)
    {
        var candidates = All(config).Where(e => exclude is null || !exclude.Contains(e)).ToList();
        if (candidates.Count == 0) return null;
        var up = candidates.Where(e => !IsDown(e)).ToList();
        var pool = up.Count > 0 ? up : candidates;   // everything down: try anyway, a server may have come back
        lock (states)
        {
            var start = (int)(Interlocked.Increment(ref rotation) % pool.Count);
            string? best = null;
            var bestLoad = int.MaxValue;
            for (var i = 0; i < pool.Count; i++)
            {
                var candidate = pool[(start + i) % pool.Count];
                var load = State(candidate).InFlight;
                if (load < bestLoad) { best = candidate; bestLoad = load; }
            }
            var state = State(best!);
            Interlocked.Increment(ref state.InFlight);
            return new Slot(best!, state);
        }
    }

    internal sealed class EndpointState(int limit)
    {
        public int InFlight;
        public long DownUntil;
        public SemaphoreSlim? Limit { get; } = limit > 0 ? new SemaphoreSlim(limit, limit) : null;
    }

    public sealed class Slot : IDisposable
    {
        private readonly EndpointState state;
        private bool acquired;
        public string Endpoint { get; }
        internal Slot(string endpoint, EndpointState state) { Endpoint = endpoint; this.state = state; }

        // Waits for this server's turn when CCTV_MODEL_CONCURRENCY is set; returns at once otherwise.
        public async Task WaitTurn(CancellationToken ct)
        {
            if (state.Limit is null) return;
            await state.Limit.WaitAsync(ct);
            acquired = true;
        }

        public void Dispose()
        {
            if (acquired) { state.Limit!.Release(); acquired = false; }
            Interlocked.Decrement(ref state.InFlight);
        }
    }
}
