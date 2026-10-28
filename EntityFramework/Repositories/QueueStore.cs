using System.Text.Json;

// JSON-backed development repository. It implements the ADS abstraction so an EF Core
// repository can replace it without changing controllers or processing services.
public sealed class QueueStore : IQueueRepository
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string root;
    private List<VideoJob> items = [];
    private ModelSettings settings = new();
    private string? linkedFolder;
    private bool running;
    private static readonly bool Fifo = Environment.GetEnvironmentVariable("CCTV_QUEUE_FIFO") is "1" or "true";
    public QueueStore(IWebHostEnvironment env)
    {
        root = Environment.GetEnvironmentVariable("CCTV_DATA_DIR") ?? Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "state.json");
        if (File.Exists(statePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(statePath), Json)
                    ?? throw new JsonException("state.json is empty.");
                settings = saved.Settings ?? new ModelSettings();
                if (settings.Mode != "local") settings = settings with { Mode = "local", Endpoint = new ModelSettings().Endpoint };   // notebook mode was removed
                linkedFolder = saved.LinkedFolder;
                items = (saved.Jobs ?? []).Select(j => j.Status == "processing" ? j with { Status = "queued", Stage = "Queued after restart" } : j).ToList();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged state file must not stop the app (and with it the cameras) from starting.
                // Keep the damaged copy for inspection and start with an empty queue.
                var backup = Path.Combine(root, $"state.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Copy(statePath, backup, true); } catch (IOException) { }
                Console.Error.WriteLine($"state.json could not be read ({ex.Message}). A copy was kept as {backup}; starting with an empty queue.");
                settings = new ModelSettings(); linkedFolder = null; items = [];
            }
        }
    }
    private DateTime lastSaveWarning = DateTime.MinValue;

    private void Persist()
    {
        var temp = Path.Combine(root, "state.tmp");
        var target = Path.Combine(root, "state.json");
        var json = JsonSerializer.Serialize(new StoredState(settings, items) { LinkedFolder = linkedFolder }, Json);
        // On Windows another process (antivirus, a sync client, the search indexer) can hold state.json open for a
        // moment, and replacing it then fails with "Access to the path is denied". That is nearly always gone a
        // moment later, so try a few times before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try { File.WriteAllText(temp, json); File.Move(temp, target, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 6) { Thread.Sleep(25 * (1 << (attempt - 1))); continue; }     // 25, 50, 100, 200, 400 ms
                // Give up for now. The state is still in memory and every later change writes all of it again, so a
                // video must not fail just because this one write did.
                if (DateTime.UtcNow - lastSaveWarning > TimeSpan.FromSeconds(30))
                {
                    lastSaveWarning = DateTime.UtcNow;
                    Console.Error.WriteLine($"state.json could not be saved ({ex.Message}). It will be written again on the next change.");
                }
                return;
            }
        }
    }
    // The UI polls this every second or so, so it gets the list without the bulky parts nobody reads there:
    // the model's raw text and the per-sample person scores (hundreds per video). Export and the stored state keep them.
    public object Snapshot() { lock (gate) return new { jobs = items.Select(Slim).ToArray(), running, settings, linkedFolder, frameLimit = VideoTools.MaxFrames }; }
    private static VideoJob Slim(VideoJob j) => j with
    {
        RawText = null,
        Screening = j.Screening is null ? null : j.Screening with { PersonSamples = [] }
    };
    public IReadOnlyList<VideoJob> Jobs() { lock (gate) return items.ToArray(); }
    public string Root => root;
    public ModelSettings Settings { get { lock (gate) return settings; } }
    public string? LinkedFolder { get { lock (gate) return linkedFolder; } }
    public void SetLinkedFolder(string? value) { lock (gate) { linkedFolder = value; Persist(); } }
    // Where this job's video actually lives: in the user's linked folder, or in the job's own
    // folder for an uploaded file. Everything downstream reads the video through here.
    public string VideoPath(VideoJob job) => job.SourcePath ?? Path.Combine(Folder(job.Id), "video");
    public bool AlreadyImported(string sourcePath)
    {
        lock (gate) return items.Any(j => j.SourcePath is not null &&
            string.Equals(j.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }
    public string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new UserError("Video not found.", 404);
        return Path.Combine(root, id);
    }
    public VideoJob Get(string id) { lock (gate) return items.FirstOrDefault(j => j.Id == id) ?? throw new UserError("Video not found.", 404); }
    public void Add(VideoJob job) { lock (gate) { items.Add(job); Persist(); } }
    public void Update(string id, Func<VideoJob, VideoJob> update)
    {
        lock (gate) { var index = items.FindIndex(j => j.Id == id); if (index >= 0) { items[index] = update(items[index]); Persist(); } }
    }
    public static readonly string[] ReviewStates = ["confirmed", "false_alarm"];
    public void SetReview(string id, string? state)
    {
        if (!string.IsNullOrEmpty(state) && !ReviewStates.Contains(state)) throw new UserError("Unknown review state.");
        lock (gate)
        {
            var job = Get(id);
            if (job.Status != "completed") throw new UserError("Only a finished video can be reviewed.", 409);
            Update(id, j => j with { ReviewState = string.IsNullOrEmpty(state) ? null : state });
        }
    }
    public void SetSettings(ModelSettings value)
    {
        lock (gate)
        {
            if (items.Any(j => j.Status == "processing")) throw new UserError("Pause and wait for the current video before changing models.", 409);
            settings = value; Persist();
        }
    }
    public void SetRunning(bool value) { lock (gate) running = value; }
    public (VideoJob Job, ModelSettings Settings)? Take()
    {
        lock (gate)
        {
            if (!running) return null;
            var index = NextQueued();
            if (index < 0) { running = false; return null; }
            items[index] = items[index] with { Status = "processing", Stage = "Extracting frames", Error = null };
            Persist(); return (items[index], settings);
        }
    }
    private int NextQueued()
    {
        if (!Fifo)
        {
            var newest = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Status != "queued" || items[i].CameraName is null) continue;
                if (newest < 0 || (items[i].SegmentStart ?? items[i].Created) >= (items[newest].SegmentStart ?? items[newest].Created)) newest = i;
            }
            if (newest >= 0) return newest;
        }
        return items.FindIndex(j => j.Status == "queued");
    }
    public void Retry(string id)
    {
        lock (gate)
        {
            var job = Get(id);
            if (job.Status is not ("failed" or "cancelled")) throw new UserError("Only failed or cancelled videos can be retried.");
            Update(id, j => j with { Status = "queued", Stage = "Queued", Error = null });
        }
    }
    public (int Removed, int Skipped) RemoveMany(IEnumerable<string> ids)
    {
        lock (gate)
        {
            var wanted = ids.ToHashSet();
            var doomed = items.Where(j => wanted.Contains(j.Id) && j.Status != "processing").Select(j => j.Id).ToHashSet();
            if (doomed.Count == 0) return (0, wanted.Count);
            items.RemoveAll(j => doomed.Contains(j.Id)); Persist();
            foreach (var id in doomed)
            {
                try { var dir = Folder(id); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } 
            }
            return (doomed.Count, wanted.Count - doomed.Count);
        }
    }
    public void Remove(string id)
    {
        lock (gate)
        {
            var job = Get(id);
            if (job.Status == "processing") throw new UserError("Cancel the video before removing it.", 409);
            // Only the job's own folder goes: a linked video is never copied here, so the
            // user's original file is untouched. Refreshing the folder re-imports it.
            items.Remove(job); Persist(); Directory.Delete(Folder(id), true);
        }
    }
}