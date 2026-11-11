using System.Text.Json;

// In-memory repository that writes through to a state store (state.json, or SQL Server with CCTV_SQLSERVER). It implements the ADS abstraction so an EF Core
// repository can replace it without changing controllers or processing services.
public sealed class QueueStore : IQueueRepository
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string root;
    private readonly IStateStore state;
    private List<VideoJob> items = [];
    private ModelSettings settings = new();
    private string? linkedFolder;
    private bool running;
    private static readonly bool Fifo = Environment.GetEnvironmentVariable("CCTV_QUEUE_FIFO") is "1" or "true";
    public QueueStore(IWebHostEnvironment env)
    {
        root = Environment.GetEnvironmentVariable("CCTV_DATA_DIR") ?? Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(root);
        state = StateStores.Create(root);
        var saved = state.Load();      // a SQL Server that cannot be reached stops the start here, on purpose
        if (saved is not null)
        {
            settings = saved.Settings ?? new ModelSettings();
            if (settings.Mode != "local") settings = settings with { Mode = "local", Endpoint = new ModelSettings().Endpoint };   // notebook mode was removed
            linkedFolder = saved.LinkedFolder;
            items = (saved.Jobs ?? []).Select(j => j.Status == "processing" ? j with { Status = "queued", Stage = "Queued after restart", Worker = null, LeaseUntil = null } : j).ToList();
        }
    }
    public IStateStore State => state;

    public event Action? Changed;

    private void Persist()
    {
        Changed?.Invoke();
        state.Save(new StoredState(settings, items) { LinkedFolder = linkedFolder });
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
    // confirmed / false_alarm judge a flagged video; missed / normal judge one that was not flagged.
    // Together they say what really happened, which is what threshold calibration needs.
    public static readonly string[] ReviewStates = ["confirmed", "false_alarm", "missed", "normal"];
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
    public bool Running { get { lock (gate) return running; } }

    public void SetRunning(bool value) { lock (gate) { running = value; Changed?.Invoke(); } }
    // The next video for a worker on this machine.
    public (VideoJob Job, ModelSettings Settings)? Take() => TakeFor(null, TimeSpan.Zero);

    // The next video for `worker` (a remote worker's name, or null for this machine). A remote worker gets a lease: it has to
    // report in (Lease) before it runs out, or the video is queued again (RequeueExpired).
    public (VideoJob Job, ModelSettings Settings)? TakeFor(string? worker, TimeSpan lease)
    {
        lock (gate)
        {
            if (!running) return null;
            var index = NextQueued();
            if (index < 0)
            {
                // With several workers, another one may still be busy with the last video: the queue is not "done" yet.
                if (!items.Any(j => j.Status == "processing")) running = false;
                return null;
            }
            items[index] = items[index] with
            {
                Status = "processing", Error = null,
                Stage = worker is null ? "Extracting frames" : "Sent to worker",
                Worker = worker, LeaseUntil = worker is null ? null : DateTimeOffset.UtcNow + lease
            };
            Persist(); return (items[index], settings);
        }
    }

    // The video `id` must be in progress on `worker`; otherwise the worker lost it (cancelled, removed, or its lease ran out
    // and another worker has it) and has to stop.
    public VideoJob Holding(string id, string worker)
    {
        lock (gate)
        {
            var job = Get(id);
            if (job.Status != "processing" || job.Worker != worker)
                throw new UserError("This video is no longer assigned to this worker.", 409);
            return job;
        }
    }

    // A worker's heartbeat: extends its lease and records where it is in the pipeline.
    public void Lease(string id, string worker, TimeSpan lease, string? stage)
    {
        lock (gate)
        {
            var job = Holding(id, worker);
            var index = items.FindIndex(j => j.Id == id);
            var changed = !string.IsNullOrWhiteSpace(stage) && stage != job.Stage;
            items[index] = job with { LeaseUntil = DateTimeOffset.UtcNow + lease, Stage = changed ? stage! : job.Stage };
            if (changed) Persist();   // the lease alone is not worth a write: a restart queues the video again anyway
        }
    }

    // Puts videos whose worker went silent back in the queue. Returns how many.
    public int RequeueExpired(DateTimeOffset now)
    {
        lock (gate)
        {
            var count = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Status != "processing" || items[i].Worker is null || items[i].LeaseUntil is not { } until || until > now) continue;
                items[i] = items[i] with { Status = "queued", Stage = "Queued (worker lost)", Worker = null, LeaseUntil = null };
                count++;
            }
            if (count > 0) Persist();
            return count;
        }
    }

    // A worker that is shutting down hands its video back.
    public void Release(string id, string worker)
    {
        lock (gate)
        {
            Holding(id, worker);
            Update(id, j => j with { Status = "queued", Stage = "Queued (worker stopped)", Worker = null, LeaseUntil = null });
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