using System.Globalization;
using System.Text;

public static class Metrics
{
    private static readonly double[] Bounds = [0.5, 1, 2, 5, 10, 30, 60, 120, 300];
    private static readonly long[] Buckets = new long[Bounds.Length + 1]; 
    private static readonly Dictionary<(string Channel, string Result), long> Deliveries = new();
    private static readonly object Gate = new();
    private static long modelCount, modelFailed;
    private static double modelSeconds;

    public static volatile int ModelUp = -1;

    public static void ModelCall(TimeSpan elapsed, bool ok)
    {
        lock (Gate)
        {
            modelCount++; modelSeconds += elapsed.TotalSeconds;
            if (!ok) modelFailed++;
            var i = Array.FindIndex(Bounds, b => elapsed.TotalSeconds <= b);
            Buckets[i < 0 ? Bounds.Length : i]++;
        }
    }

    public static void Delivery(string channel, bool ok)
    {
        lock (Gate) { var key = (channel, ok ? "ok" : "failed"); Deliveries[key] = Deliveries.GetValueOrDefault(key) + 1; }
    }

    private static string Esc(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Render(IReadOnlyList<VideoJob> jobs, bool running, IEnumerable<CameraStatus> cameras, int pendingAlerts, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        void Head(string name, string type, string help) => sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append("\n# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        void Line(string name, double value, string? labels = null) =>
            sb.Append(name).Append(labels is null ? "" : "{" + labels + "}").Append(' ').Append(Num(value)).Append('\n');

        Head("cctv_jobs", "gauge", "Videos in the queue, by status.");
        foreach (var status in new[] { "queued", "processing", "completed", "failed", "cancelled" })
            Line("cctv_jobs", jobs.Count(j => j.Status == status), $"status=\"{status}\"");
        Head("cctv_jobs_flagged", "gauge", "Finished videos flagged suspicious or weapon.");
        Line("cctv_jobs_flagged", jobs.Count(j => j.Status == "completed" && (j.Suspicious == "yes" || j.Weapon == "yes")));
        Head("cctv_queue_running", "gauge", "1 while the queue is running, 0 while paused.");
        Line("cctv_queue_running", running ? 1 : 0);
        Head("cctv_queue_oldest_waiting_seconds", "gauge", "Age of the oldest video still waiting.");
        var waiting = jobs.Where(j => j.Status == "queued").Select(j => (j.SegmentStart ?? j.Created)).ToList();
        Line("cctv_queue_oldest_waiting_seconds", waiting.Count == 0 ? 0 : Math.Max(0, (now - waiting.Min()).TotalSeconds));

        Head("cctv_alerts_failed_jobs", "gauge", "Videos whose last notification failed on at least one channel.");
        Line("cctv_alerts_failed_jobs", jobs.Count(j => !string.IsNullOrEmpty(j.NotifyError)));
        Head("cctv_alerts_pending", "gauge", "Alerts still waiting to be delivered (including retries).");
        Line("cctv_alerts_pending", pendingAlerts);

        Head("cctv_camera_up", "gauge", "1 while a camera is delivering data.");
        foreach (var camera in cameras) Line("cctv_camera_up", camera.State == "running" ? 1 : 0, $"camera=\"{Esc(camera.Camera)}\"");

        if (ModelUp >= 0) { Head("cctv_model_up", "gauge", "1 when the model server answers, 0 when it does not."); Line("cctv_model_up", ModelUp); }

        lock (Gate)
        {
            Head("cctv_alert_deliveries_total", "counter", "Notification deliveries by channel and result.");
            foreach (var ((channel, result), count) in Deliveries.OrderBy(d => d.Key))
                Line("cctv_alert_deliveries_total", count, $"channel=\"{Esc(channel)}\",result=\"{result}\"");

            Head("cctv_model_request_duration_seconds", "histogram", "Time of one model request, including retries inside it.");
            long cumulative = 0;
            for (var i = 0; i < Bounds.Length; i++) { cumulative += Buckets[i]; Line("cctv_model_request_duration_seconds_bucket", cumulative, $"le=\"{Num(Bounds[i])}\""); }
            Line("cctv_model_request_duration_seconds_bucket", modelCount, "le=\"+Inf\"");
            Line("cctv_model_request_duration_seconds_sum", modelSeconds);
            Line("cctv_model_request_duration_seconds_count", modelCount);
            Head("cctv_model_requests_failed_total", "counter", "Model requests that ended in an error.");
            Line("cctv_model_requests_failed_total", modelFailed);
        }
        return sb.ToString();
    }
}