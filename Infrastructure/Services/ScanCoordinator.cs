public sealed class ScanCoordinator
{
	public sealed record ManualScan(DateTimeOffset FinishedAt, int Added, int Skipped, List<string> Errors);

	private readonly SemaphoreSlim wake = new(0, 1);
	private readonly object gate = new();
	private bool pending, scanning;
	private ManualScan? last;

	public void Request()
	{
		lock (gate) pending = true;
		try { wake.Release(); } catch (SemaphoreFullException) { } 
	}

	// Wakes the watcher for a poll without asking for a linked-folder scan (a new live clip is waiting in the RTSP folder).
	public void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }

	public bool TryTake() { lock (gate) { var was = pending; pending = false; return was; } }
	public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => wake.WaitAsync(timeout, ct);
	public void Begin() { lock (gate) scanning = true; }
	public void Finish(ScanResult? result)
	{
		lock (gate)
		{
			scanning = false;
			if (result is not null) last = new ManualScan(DateTimeOffset.UtcNow, result.Added, result.Skipped, result.Errors);
		}
	}

	public object Status() { lock (gate) return new { pending, scanning, last }; }
}