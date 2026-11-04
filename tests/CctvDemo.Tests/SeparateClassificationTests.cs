using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

// The "separate classification" call: the model gets the frames once more and answers only suspicious / weapon,
// with no narrative to write first. These tests use a stub model server, so no Ollama is needed.
public class SeparateClassificationTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("cctv-flag-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    // A frame file whose content is its own name, so a request body can be traced back to the frame group it carried.
    private string Frame(string marker)
    {
        var path = Path.Combine(dir, marker + ".png");
        File.WriteAllText(path, marker);
        return path;
    }

    private static string B64(string marker) => Convert.ToBase64String(Encoding.UTF8.GetBytes(marker));

    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Enqueue(body);
            return respond(body);
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // An OpenAI-style answer {"suspicious":..,"weapon":..}. With pYes set, the value token carries yes/no alternatives
    // so Scoring.PYes can read a probability, the way Ollama's logprobs arrive.
    private static string Reply(string suspicious, string weapon, double? pYes)
    {
        var content = JsonSerializer.Serialize(new { suspicious, weapon });
        object? logprobs = null;
        if (pYes is { } p)
        {
            var top = new object[] { new { token = "yes", logprob = Math.Log(p) }, new { token = "no", logprob = Math.Log(1 - p) } };
            logprobs = new
            {
                content = new object[]
                {
                    new { token = "{\"suspicious\":\"", logprob = -0.1, top_logprobs = Array.Empty<object>() },
                    new { token = suspicious, logprob = Math.Log(suspicious == "yes" ? p : 1 - p), top_logprobs = top },
                    new { token = "\",\"weapon\":\"", logprob = -0.1, top_logprobs = Array.Empty<object>() },
                    new { token = weapon, logprob = -0.1, top_logprobs = Array.Empty<object>() },
                    new { token = "\"}", logprob = -0.1, top_logprobs = Array.Empty<object>() }
                }
            };
        }
        return JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop", logprobs } } });
    }

    private static string RawReply(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } });

    // ---- the problem this call is meant to fix: a timid model says "no" but still leans a little towards "yes" ----

    [Fact]
    public async Task A_timid_no_still_yields_the_probability_that_a_lower_threshold_can_use()
    {
        var handler = new StubHandler(_ => Ok(Reply("no", "no", 0.35)));
        using var client = new HttpClient(handler);

        var verdict = await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None);

        Assert.Equal("no", verdict.Suspicious);
        Assert.Equal(0.35, verdict.Score!.Value, 2);
        // the model's own text is "no"; only the threshold decides whether this video is flagged
        Assert.Equal("no", Scoring.Decide(verdict.Suspicious, verdict.Score, 0.5).Decision);
        Assert.Equal("yes", Scoring.Decide(verdict.Suspicious, verdict.Score, 0.25).Decision);
    }

    [Fact]
    public async Task Without_probabilities_the_score_is_missing_and_the_models_own_answer_is_kept()
    {
        var handler = new StubHandler(_ => Ok(Reply("yes", "no", null)));
        using var client = new HttpClient(handler);

        var verdict = await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None);

        Assert.Null(verdict.Score);
        Assert.Equal("yes", Scoring.Decide(verdict.Suspicious, verdict.Score, 0.9).Decision);
    }

    // ---- what is sent ----

    [Fact]
    public async Task The_request_is_a_short_flags_only_call_with_probabilities_on()
    {
        var handler = new StubHandler(_ => Ok(Reply("no", "no", 0.1)));
        using var client = new HttpClient(handler);

        await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"max_tokens\":40", body);
        Assert.Contains("\"logprobs\":true", body);
        Assert.Contains("\"temperature\":0", body);
        Assert.DoesNotContain("activity_description", body);   // no narrative to write: that is the whole point of the split
        Assert.Contains(B64("a"), body);
    }

    [Fact]
    public async Task The_scene_note_is_sent_only_in_scene_mode()
    {
        var handler = new StubHandler(_ => Ok(Reply("no", "no", 0.1)));
        using var client = new HttpClient(handler);

        await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None);
        await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], true, CancellationToken.None);

        var bodies = handler.Bodies.ToArray();
        Assert.DoesNotContain("MODE ADEGAN", bodies[0]);
        Assert.Contains("MODE ADEGAN", bodies[1]);
    }

    [Fact]
    public void The_prompt_still_says_to_answer_yes_when_in_doubt_even_though_small_models_ignore_it()
    {
        // Documents the instruction the evaluation found Gemma ignoring (15.8% yes). If this wording is ever
        // removed, the recall comparison with the old runs is no longer like for like.
        var question = FlagClassifier.Question(new ModelSettings(), false);
        Assert.Contains("Jika ragu", question);
        Assert.Contains("\"yes\"", question);
    }

    [Fact]
    public void Site_context_is_added_only_when_it_is_switched_on()
    {
        Assert.DoesNotContain("gudang", FlagClassifier.Question(new ModelSettings { SiteContext = "gudang beras", SiteContextEnabled = false }, false));
        Assert.Contains("gudang beras", FlagClassifier.Question(new ModelSettings { SiteContext = "gudang beras", SiteContextEnabled = true }, false));
    }

    // ---- answers that must not be guessed at ----

    [Fact]
    public async Task An_invalid_flag_is_refused_not_guessed()
    {
        var handler = new StubHandler(_ => Ok(RawReply("""{"suspicious":"maybe","weapon":"no"}""")));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<UserError>(() =>
            FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None));

        Assert.Equal(502, error.Status);
    }

    [Fact]
    public async Task A_missing_flag_is_refused_not_guessed()
    {
        var handler = new StubHandler(_ => Ok(RawReply("""{"suspicious":"yes"}""")));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<UserError>(() =>
            FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None));
    }

    [Fact]
    public async Task The_weapon_flag_is_forced_to_no_when_the_weapon_check_is_off()
    {
        var handler = new StubHandler(_ => Ok(Reply("yes", "yes", 0.9)));
        using var client = new HttpClient(handler);

        var off = await FlagClassifier.Classify(client, new ModelSettings { WeaponEnabled = false }, [Frame("a")], false, CancellationToken.None);
        var on = await FlagClassifier.Classify(client, new ModelSettings { WeaponEnabled = true }, [Frame("a")], false, CancellationToken.None);

        Assert.Equal("no", off.Weapon);
        Assert.Equal("yes", on.Weapon);
    }

    [Fact]
    public async Task A_server_that_rejects_probabilities_costs_one_retry_without_them()
    {
        var calls = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
              { Content = new StringContent("""{"error":{"message":"logprobs is not supported for this model"}}""", Encoding.UTF8, "application/json") }
            : Ok(Reply("yes", "no", null)));
        using var client = new HttpClient(handler);

        var verdict = await FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None);

        var bodies = handler.Bodies.ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.Contains("\"logprobs\":true", bodies[0]);
        Assert.Contains("\"logprobs\":false", bodies[1]);
        Assert.Null(verdict.Score);
        Assert.Equal("yes", verdict.Suspicious);
    }

    [Fact]
    public async Task Any_other_server_error_is_not_swallowed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("""{"error":"boom"}""", Encoding.UTF8, "application/json") });
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<UserError>(() =>
            FlagClassifier.Classify(client, new ModelSettings(), [Frame("a")], false, CancellationToken.None));

        Assert.Single(handler.Bodies);   // no retry
        Assert.Contains("500", error.Message);
    }

    // ---- several frame groups ----

    [Fact]
    public async Task Run_asks_once_per_group_and_keeps_the_strongest_evidence()
    {
        var handler = new StubHandler(body =>
            body.Contains(B64("g-b")) ? Ok(Reply("yes", "no", 0.72)) :
            body.Contains(B64("g-c")) ? Ok(Reply("no", "yes", 0.30)) :
            Ok(Reply("no", "no", 0.10)));
        using var client = new HttpClient(handler);
        var progress = new ConcurrentQueue<(int Done, int Total)>();
        var groups = new List<string[]> { new[] { Frame("g-a") }, new[] { Frame("g-b") }, new[] { Frame("g-c") } };

        var verdict = await FlagClassifier.Run(client, new ModelSettings { LlmConcurrency = 3 }, groups, false,
            (done, total) => progress.Enqueue((done, total)), CancellationToken.None);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal("yes", verdict.Suspicious);
        Assert.Equal(0.72, verdict.Score!.Value, 2);   // the highest p(yes), not an average that hides one alarming frame group
        Assert.Equal("yes", verdict.Weapon);           // a weapon in any group counts
        Assert.Equal(3, progress.Count);
        Assert.All(progress, p => Assert.Equal(3, p.Total));
        Assert.Contains((3, 3), progress);
    }

    [Fact]
    public async Task Run_with_only_quiet_groups_stays_no_and_keeps_the_highest_score()
    {
        var handler = new StubHandler(body => body.Contains(B64("q-2")) ? Ok(Reply("no", "no", 0.40)) : Ok(Reply("no", "no", 0.05)));
        using var client = new HttpClient(handler);
        var groups = new List<string[]> { new[] { Frame("q-1") }, new[] { Frame("q-2") } };

        var verdict = await FlagClassifier.Run(client, new ModelSettings(), groups, false, (_, _) => { }, CancellationToken.None);

        Assert.Equal("no", verdict.Suspicious);
        Assert.Equal(0.40, verdict.Score!.Value, 2);
        // this is the case where a calibrated threshold of, say, 0.3 turns an unflagged video into a flagged one
        Assert.Equal("yes", Scoring.Decide(verdict.Suspicious, verdict.Score, 0.3).Decision);
    }
}
