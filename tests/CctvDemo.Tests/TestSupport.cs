using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal sealed class EnvScope : IDisposable
{
    private readonly List<(string Name, string? Old)> saved = [];

    public EnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
        {
            saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        for (var i = saved.Count - 1; i >= 0; i--) Environment.SetEnvironmentVariable(saved[i].Name, saved[i].Old);
    }
}

internal sealed class FakeEnv(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "CctvDemo.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestApp : IDisposable
{
    private readonly EnvScope env;
    public string Dir { get; }
    public QueueStore Store { get; }
    public RtspIngestService Rtsp { get; }

    public TestApp(params (string Name, string? Value)[] overrides)
    {
        Dir = Directory.CreateTempSubdirectory("cctv-tests-").FullName;
        var vars = new Dictionary<string, string?>
        {
            ["CCTV_DATA_DIR"] = Path.Combine(Dir, "data"),
            ["CCTV_RTSP_DIR"] = null,
            ["CCTV_CAMERAS"] = null,
            ["CCTV_MAX_QUEUE_PER_CAMERA"] = "20",
            ["CCTV_KEEP_EMPTY_MINUTES"] = "10",
            ["CCTV_KEEP_HOURS"] = "24",
            ["CCTV_KEEP_FLAGGED_DAYS"] = "30",
            ["CCTV_MAX_DISK_GB"] = "20",
            ["CCTV_KEEP_ORPHAN_MINUTES"] = "60"
        };
        foreach (var (name, value) in overrides) vars[name] = value;
        env = new EnvScope(vars.Select(kv => (kv.Key, kv.Value)).ToArray());

        var host = new FakeEnv(Dir);
        Store = new QueueStore(host);
        Rtsp = new RtspIngestService(Store, new VideoTools(host), Options.Create(new RtspOptions()), NullLogger<RtspIngestService>.Instance);
    }

    public VideoJob Segment(string camera, string file, DateTimeOffset created, Func<VideoJob, VideoJob>? change = null, int bytes = 10)
    {
        var dir = Path.Combine(Rtsp.OutputRoot, camera);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllBytes(path, new byte[bytes]);
        var job = new VideoJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = file,
            SourcePath = path,
            CameraName = camera,
            Status = "completed",
            Stage = "Done",
            Created = created,
            SegmentStart = created,
            Duration = 60
        };
        if (change is not null) job = change(job);
        Directory.CreateDirectory(Store.Folder(job.Id));
        Store.Add(job);
        return job;
    }

    public void Dispose()
    {
        env.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("The expected condition was not reached in time.");
            await Task.Delay(10);
        }
    }
}

internal sealed class FakeNotifier(string name = "fake", bool enabled = true, int failTimes = 0) : INotifier
{
    private int attempts;
    public string Name => name;
    public bool Enabled => enabled;
    public int Attempts => attempts;
    public ConcurrentQueue<NotificationPayload> Sent { get; } = new();

    public Task SendAsync(NotificationPayload payload, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref attempts);
        if (failTimes < 0 || n <= failTimes) throw new NotifierException("boom");
        Sent.Enqueue(payload);
        return Task.CompletedTask;
    }
}

internal sealed record SinkRecord(string JobId, DateTimeOffset? NotifiedAt, string? Error);

internal sealed class FakeSink : INotificationResultSink
{
    public ConcurrentQueue<SinkRecord> Records { get; } = new();
    public Task RecordAsync(string jobId, DateTimeOffset? notifiedAt, string? error, CancellationToken ct)
    {
        Records.Enqueue(new SinkRecord(jobId, notifiedAt, error));
        return Task.CompletedTask;
    }
}