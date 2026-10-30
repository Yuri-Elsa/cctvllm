using System.Text.Json;

public static class LlmPersonDetector
{
    private const string SystemPrompt =
        "Anda memeriksa apakah ada manusia pada sebuah gambar kamera pengawas. Jawab hanya dengan JSON {\"person\": \"yes\" atau \"no\"}.";
    private const string Question =
        "Apakah ada setidaknya satu orang yang terlihat pada gambar ini, termasuk yang jauh, kecil, atau terlihat sebagian? Jawab yes atau no.";

    public static async Task<Detection> Run(
        VideoTools videos, IHttpClientFactory clients, VideoJob job, string videoPath, string folder,
        ModelSettings config, double modelGap, string root, Action<string> stage, CancellationToken ct)
    {
        if (!File.Exists(videoPath))
            throw new UserError("This video is no longer at its original path in the linked folder.", 404);
        var interval = Math.Max(modelGap, job.Duration / VideoTools.MaxFrames);
        var frames = await videos.SampleFrames(videoPath, folder, interval, ct);
        using var client = ModelCalls.CreateClient(clients);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var motionTask = MotionTimeline(root, folder, videoPath, job.Fps, linked.Token);
        double[] scores;
        try
        {
            scores = await ScoreAll(client, config, frames.Paths,
                (done, total) => stage($"Detecting people (LLM) {done}/{total}"), ct);
        }
        catch
        {
            linked.Cancel();
            try { await motionTask; } catch { }
            throw;
        }
        var motion = await motionTask;
        var samples = new List<PersonSample>();
        var paths = new List<string>(); var times = new List<double>();
        for (var i = 0; i < frames.Paths.Length; i++)
        {
            samples.Add(new PersonSample(frames.Timestamps[i], Math.Round(scores[i], 4)));
            if (scores[i] >= config.PersonThreshold) { paths.Add(frames.Paths[i]); times.Add(frames.Timestamps[i]); }
        }
        return new Detection(paths.ToArray(), paths.ToArray(), times.ToArray(), "[]", motion, samples.ToArray(),
            LlmChecked: frames.Paths.Length, LlmRejected: frames.Paths.Length - paths.Count);
    }

    private static async Task<string> MotionTimeline(string root, string folder, string videoPath, double fps, CancellationToken ct)
    {
        try
        {
            var tracked = await PersonTracking.Track(root, folder, videoPath, fps, PersonTracking.MotionConf,
                PersonTracking.ModelMinGap, null, true, ct);
            return tracked.Motion;
        }
        catch (UserError ex) when (ex.Status == 503) { return "[]"; }
    }

    internal static async Task<double[]> ScoreAll(HttpClient client, ModelSettings config, IReadOnlyList<string> paths,
        Action<int, int> progress, CancellationToken ct)
    {
        var scores = new double[paths.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.LlmConcurrency, 1, 8), CancellationToken = ct },
            async (i, token) =>
            {
                scores[i] = await Score(client, config, paths[i], token);
                progress(Interlocked.Increment(ref done), paths.Count);
            });
        return scores;
    }

    internal static async Task<double> Score(HttpClient client, ModelSettings config, string path, CancellationToken ct)
    {
        var image = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct));
        object Request(bool scores) => new
        {
            model = config.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = image } },
                        new { type = "text", text = Question }
                    }
                }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "person_present",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new { person = new { type = "string", @enum = new[] { "yes", "no" } } },
                        required = new[] { "person" }
                    }
                }
            },
            max_tokens = 30,
            temperature = 0,
            seed = 7,
            reasoning_effort = "none",
            logprobs = scores,
            top_logprobs = scores ? Scoring.TopLogprobs : 0
        };
        using var doc = await ModelCalls.Ask(client, config, Request, true, ct);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var raw = ModelCalls.Content(choice.GetProperty("message"));
        using var parsed = DescriptionWorker.ParseObject(raw);
        var answer = parsed.RootElement.TryGetProperty("person", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() : null;
        if (answer is not ("yes" or "no"))
            throw new UserError("The model returned an invalid person-detection answer. Retry this video.", 502);
        return Scoring.Effective(answer, Scoring.PYes(choice, "person"));
    }
}