using System.Text.Json;
using System.Text.Json.Nodes;

public static class HybridPersonDetector
{
    public static async Task<Detection> Run(
        IHttpClientFactory clients, VideoJob job, string videoPath, string folder, ModelSettings config,
        double modelGap, string root, Action<string> stage, CancellationToken ct)
    {
        var low = Math.Min(config.PersonVerifyLow, config.PersonThreshold);
        var tracked = await PersonTracking.Track(root, folder, videoPath, job.Fps, config.PersonThreshold, modelGap, low, false, ct);

        var pending = tracked.Frames.Where(f => f.Uncertain).ToArray();
        var verdicts = new Dictionary<double, bool>();
        if (pending.Length > 0)
        {
            using var client = ModelCalls.CreateClient(clients);
            var scores = await LlmPersonDetector.ScoreAll(client, config, pending.Select(f => f.PlainPath).ToArray(),
                (done, total) => stage($"Verifying people (LLM) {done}/{total}"), ct);
            for (var i = 0; i < pending.Length; i++) verdicts[pending[i].Timestamp] = scores[i] >= config.HybridLlmThreshold;
        }

        var kept = tracked.Frames.Where(f => !f.Uncertain || verdicts[f.Timestamp]).ToArray();
        var accepted = pending.Where(f => verdicts[f.Timestamp]).Select(f => f.Timestamp).ToArray();
        var motion = DropRejectedMotion(tracked.Motion, accepted, modelGap);
        var shaped = PersonTracking.Shape(kept, motion, tracked.Samples);
        return shaped with { LlmChecked = pending.Length, LlmRejected = pending.Length - accepted.Length };
    }

    internal static string DropRejectedMotion(string motion, double[] acceptedTimes, double window)
    {
        if (JsonNode.Parse(string.IsNullOrWhiteSpace(motion) ? "[]" : motion) is not JsonArray entries) return "[]";
        var result = new JsonArray();
        foreach (var entry in entries)
        {
            if (entry is not JsonObject item) continue;
            var uncertain = item["uncertain"]?.GetValue<bool>() == true;
            var t = item["timestamp"]?.GetValue<double>() ?? 0;
            if (uncertain && !acceptedTimes.Any(a => Math.Abs(a - t) < window - 1e-9)) continue;
            item.Remove("uncertain");
            result.Add(item.DeepClone());
        }
        return result.ToJsonString();
    }
}