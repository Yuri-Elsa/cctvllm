using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public class AgentPolicyTests
{
    private static VideoJob Job(string weapon = "no", string? description = "Seseorang memanjat pagar.") => new()
    {
        Id = "j1", Name = "v.mp4", CameraName = "lobby", Status = "completed",
        Suspicious = "yes", Weapon = weapon, Description = description
    };

    private static AgentContext Ctx(string[]? channels = null, bool more = true, VideoJob? job = null) =>
        new(job ?? Job(), new ModelSettings(), channels ?? ["whatsapp", "email", "webhook", "log"], more);

    // ---- allowed actions ----

    [Fact]
    public void Allowed_actions_follow_the_enabled_channels()
    {
        Assert.Equal(new[] { "send_whatsapp", "send_email", "ignore", "request_more_frames" }, AgentPolicy.AllowedActions(Ctx()));
        Assert.Equal(new[] { "send_email", "ignore" }, AgentPolicy.AllowedActions(Ctx(["email", "log"], more: false)));
        Assert.Equal(new[] { "send_whatsapp", "ignore", "request_more_frames" }, AgentPolicy.AllowedActions(Ctx(["whatsapp"])));
    }

    [Fact]
    public void The_agent_only_has_a_choice_when_whatsapp_or_email_is_enabled()
    {
        Assert.True(AgentPolicy.HasChoice(["whatsapp"]));
        Assert.True(AgentPolicy.HasChoice(["email", "log"]));
        Assert.False(AgentPolicy.HasChoice(["webhook", "log"]));
        Assert.False(AgentPolicy.HasChoice([]));
    }

    // ---- validation of what the model returned ----

    [Fact]
    public void A_valid_answer_is_normalised()
    {
        var d = AgentPolicy.Validate("  SEND_WHATSAPP ", " High ", "  Kamera   lobby:\n orang memanjat. ", "  alasan ", Ctx());
        Assert.Equal("send_whatsapp", d.Action);
        Assert.Equal("high", d.Urgency);
        Assert.Equal("Kamera lobby: orang memanjat.", d.Summary);   // whitespace collapsed
        Assert.Equal("alasan", d.Reason);
        Assert.False(d.GuardOverride);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("send_sms")]
    [InlineData("call_police")]
    public void An_unknown_or_missing_action_is_rejected(string? action) =>
        Assert.Throws<AgentException>(() => AgentPolicy.Validate(action, "low", "x", "r", Ctx()));

    [Fact]
    public void An_action_for_a_channel_that_is_not_enabled_is_rejected()
    {
        Assert.Throws<AgentException>(() => AgentPolicy.Validate("send_whatsapp", "low", "x", "r", Ctx(["email"])));
        Assert.Throws<AgentException>(() => AgentPolicy.Validate("send_email", "low", "x", "r", Ctx(["whatsapp"])));
    }

    [Fact]
    public void Requesting_more_frames_is_rejected_once_it_has_been_used()
    {
        Assert.Throws<AgentException>(() => AgentPolicy.Validate("request_more_frames", "low", "", "r", Ctx(more: false)));
        Assert.Equal("request_more_frames", AgentPolicy.Validate("request_more_frames", "low", "", "r", Ctx()).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("urgent")]
    [InlineData("")]
    public void An_invalid_urgency_is_rejected(string? urgency) =>
        Assert.Throws<AgentException>(() => AgentPolicy.Validate("ignore", urgency, "", "r", Ctx()));

    [Theory]
    [InlineData("send_whatsapp")]
    [InlineData("send_email")]
    public void Sending_without_a_summary_is_rejected(string action)
    {
        Assert.Throws<AgentException>(() => AgentPolicy.Validate(action, "high", "   ", "r", Ctx()));
        Assert.Throws<AgentException>(() => AgentPolicy.Validate(action, "high", null, "r", Ctx()));
    }

    [Fact]
    public void Ignoring_needs_no_summary()
    {
        var d = AgentPolicy.Validate("ignore", "low", null, null, Ctx());
        Assert.Equal("ignore", d.Action);
        Assert.Equal("", d.Summary);
        Assert.Equal("", d.Reason);
    }

    [Fact]
    public void A_long_summary_is_cut_to_the_limit()
    {
        var d = AgentPolicy.Validate("send_email", "low", new string('a', 1000), "r", Ctx());
        Assert.Equal(AgentPolicy.MaxSummaryChars + 1, d.Summary.Length);   // limit plus the ellipsis
        Assert.EndsWith("…", d.Summary);
        var exact = AgentPolicy.Validate("send_email", "low", new string('a', AgentPolicy.MaxSummaryChars), "r", Ctx());
        Assert.Equal(AgentPolicy.MaxSummaryChars, exact.Summary.Length);   // exactly at the limit is untouched
    }

    // ---- safety guard: a weapon is never ignored ----

    [Fact]
    public void A_weapon_cannot_be_ignored_and_goes_to_whatsapp_as_critical()
    {
        var ctx = Ctx(job: Job("yes"));
        var g = AgentPolicy.Guard(new AgentDecision("ignore", "low", "", "tidak penting"), ctx);
        Assert.Equal("send_whatsapp", g.Action);
        Assert.Equal("critical", g.Urgency);
        Assert.True(g.GuardOverride);
        Assert.Equal("Seseorang memanjat pagar.", g.Summary);   // falls back to the model's description
        Assert.Contains("tidak penting", g.Reason);
    }

    [Fact]
    public void The_guard_uses_email_when_whatsapp_is_not_enabled()
    {
        var ctx = Ctx(["email", "log"], job: Job("yes"));
        Assert.Equal("send_email", AgentPolicy.Guard(new AgentDecision("ignore", "low", "", ""), ctx).Action);
    }

    [Fact]
    public void The_guard_keeps_the_agents_own_summary_and_caps_a_long_description()
    {
        var ctx = Ctx(job: Job("yes", new string('d', 900)));
        Assert.Equal("ringkasan agen", AgentPolicy.Guard(new AgentDecision("ignore", "low", "ringkasan agen", ""), ctx).Summary);
        var capped = AgentPolicy.Guard(new AgentDecision("ignore", "low", "", ""), ctx).Summary;
        Assert.Equal(AgentPolicy.MaxSummaryChars + 1, capped.Length);
    }

    [Theory]
    [InlineData("no", "ignore")]                  // no weapon: ignoring is the agent's call
    [InlineData("yes", "send_email")]             // weapon but already sent: nothing to correct
    [InlineData("yes", "send_whatsapp")]
    [InlineData("yes", "request_more_frames")]
    public void The_guard_leaves_every_other_decision_alone(string weapon, string action)
    {
        var decision = new AgentDecision(action, "low", "s", "r");
        Assert.Same(decision, AgentPolicy.Guard(decision, Ctx(job: Job(weapon))));
    }

    // ---- routing ----

    [Fact]
    public void Routing_picks_the_chosen_channel_plus_webhook_and_log()
    {
        Assert.Equal(new[] { "whatsapp", "webhook", "log" }, AgentPolicy.Route(new AgentDecision("send_whatsapp", "high", "s", "r")));
        Assert.Equal(new[] { "email", "webhook", "log" }, AgentPolicy.Route(new AgentDecision("send_email", "low", "s", "r")));
    }

    [Fact]
    public void An_ignored_flag_prefers_email_then_webhook_and_never_whatsapp()
    {
        Assert.Equal(new[] { "email" }, AgentPolicy.RouteIgnored(new[] { "whatsapp", "email", "webhook", "log" }));
        Assert.Equal(new[] { "webhook" }, AgentPolicy.RouteIgnored(new[] { "whatsapp", "webhook", "log" }));
        Assert.Null(AgentPolicy.RouteIgnored(new[] { "whatsapp", "log" }));
    }

    [Fact]
    public void Routing_is_open_for_non_send_decisions_and_for_guard_overrides()
    {
        Assert.Null(AgentPolicy.Route(null));
        Assert.Null(AgentPolicy.Route(new AgentDecision("ignore", "low", "", "")));   // ignore is routed by RouteIgnored instead
        // A guard override reaches every channel, so a weapon alert is never narrowed to one.
        Assert.Null(AgentPolicy.Route(new AgentDecision("send_whatsapp", "critical", "s", "r", GuardOverride: true)));
    }
}

public class FlagNotifierTests
{
    private sealed class FakeAgent(params Func<AgentContext, AgentDecision>[] answers) : INotificationAgent
    {
        private int calls;
        public List<AgentContext> Contexts { get; } = [];
        public int Calls => calls;
        public Task<AgentDecision> DecideAsync(AgentContext context, CancellationToken ct)
        {
            Contexts.Add(context);
            var answer = answers[Math.Min(Interlocked.Increment(ref calls) - 1, answers.Length - 1)];
            return Task.FromResult(answer(context));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TestApp App { get; } = new();
        public FakeNotifier WhatsApp { get; } = new("whatsapp");
        public FakeNotifier Email { get; } = new("email");
        public FakeNotifier Log { get; } = new("log");
        public FakeNotifier Webhook { get; }
        public NotificationDispatcher Dispatcher { get; }
        public FlagNotifier Flags { get; }
        public FakeAgent Agent { get; }

        public Harness(FakeAgent agent, bool whatsApp = true, bool email = true)
        {
            Agent = agent;
            Webhook = new FakeNotifier("webhook", enabled: false);
            var options = new NotificationOptions
            {
                ThrottleWindow = TimeSpan.Zero, FlushInterval = TimeSpan.FromMilliseconds(20),
                BackoffBase = TimeSpan.FromMilliseconds(1), MaxAttempts = 1
            };
            var notifiers = new INotifier[]
            {
                whatsApp ? WhatsApp : new FakeNotifier("whatsapp", enabled: false),
                email ? Email : new FakeNotifier("email", enabled: false),
                Log, Webhook
            };
            Dispatcher = new NotificationDispatcher(notifiers, new FakeSink(), Options.Create(options), NullLogger<NotificationDispatcher>.Instance);
            Dispatcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Flags = new FlagNotifier(Dispatcher, agent, App.Store, NullLogger<FlagNotifier>.Instance);
        }

        public VideoJob Add(string id = "job", string suspicious = "yes", string weapon = "no") =>
            App.Segment("lobby", id + ".mp4", DateTimeOffset.UtcNow, j => j with
            {
                Suspicious = suspicious, Weapon = weapon, Description = "Seseorang memanjat pagar.",
                Screening = new ScreeningLog { SuspiciousScore = 0.55, SuspiciousThreshold = 0.5 }
            });

        public int Sent => WhatsApp.Sent.Count + Email.Sent.Count;

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.StopAsync(CancellationToken.None);
            Dispatcher.Dispose();
            App.Dispose();
        }
    }

    private static readonly ModelSettings Local = new();

    private static Func<AgentContext, AgentDecision> Says(string action, string urgency = "high", string summary = "Kamera lobby: orang memanjat pagar.") =>
        _ => new AgentDecision(action, urgency, action.StartsWith("send") ? summary : "", "karena begitu");

    private static Task Notify(Harness h, VideoJob job, ModelSettings? config = null, Func<string, CancellationToken, Task>? reanalyze = null) =>
        h.Flags.NotifyAsync(h.App.Store.Get(job.Id), config ?? Local, reanalyze ?? ((_, _) => Task.CompletedTask), CancellationToken.None);

    // ---- when the agent is not involved ----

    [Fact]
    public async Task An_unflagged_video_notifies_nobody_and_never_calls_the_agent()
    {
        await using var h = new Harness(new FakeAgent(Says("send_whatsapp")));
        var job = h.Add(suspicious: "no");
        await Notify(h, job);
        await Task.Delay(100);
        Assert.Equal(0, h.Agent.Calls);
        Assert.Equal(0, h.Sent);
    }

    [Fact]
    public async Task With_the_agent_switched_off_the_legacy_dispatcher_sends_to_every_channel()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore")));
        var job = h.Add();
        await Notify(h, job, Local with { AgentEnabled = false });
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1 && h.Email.Sent.Count == 1);
        Assert.Equal(0, h.Agent.Calls);
        Assert.Null(h.App.Store.Get(job.Id).Screening!.Agent);
    }

    [Fact]
    public async Task Notebook_mode_always_uses_the_legacy_dispatcher()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore")));
        var job = h.Add();
        await Notify(h, job, Local with { Mode = "notebook" });
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1 && h.Email.Sent.Count == 1);
        Assert.Equal(0, h.Agent.Calls);
    }

    [Fact]
    public async Task Without_a_whatsapp_or_email_channel_there_is_nothing_to_choose_so_the_legacy_path_runs()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore")), whatsApp: false, email: false);
        var job = h.Add();
        await Notify(h, job);
        await Wait.Until(() => h.Log.Sent.Count == 1);
        Assert.Equal(0, h.Agent.Calls);
    }

    // ---- the agent decides ----

    [Fact]
    public async Task A_whatsapp_decision_sends_only_to_whatsapp_with_the_agents_summary_and_urgency()
    {
        await using var h = new Harness(new FakeAgent(Says("send_whatsapp", "high")));
        var job = h.Add();
        await Notify(h, job);
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1);
        await Task.Delay(100);
        Assert.Empty(h.Email.Sent);
        var payload = h.WhatsApp.Sent.Single();
        Assert.Equal("high", payload.Urgency);
        Assert.Equal("Kamera lobby: orang memanjat pagar.", payload.Summary);

        var log = h.App.Store.Get(job.Id).Screening!;
        Assert.Equal("send_whatsapp", log.Agent!.Action);
        Assert.Equal("high", log.Agent.Urgency);
        Assert.False(log.Agent.FallbackUsed);
        Assert.Equal(0.55, log.SuspiciousScore);   // the existing screening data is kept, not replaced
    }

    [Fact]
    public async Task An_email_decision_sends_only_to_email()
    {
        await using var h = new Harness(new FakeAgent(Says("send_email", "low")));
        await Notify(h, h.Add());
        await Wait.Until(() => h.Email.Sent.Count == 1);
        await Task.Delay(100);
        Assert.Empty(h.WhatsApp.Sent);
    }

    [Fact]
    public async Task An_ignore_decision_still_goes_out_as_a_low_priority_email_and_never_to_whatsapp()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore", "critical")));   // urgency is forced to low
        var job = h.Add();
        await Notify(h, job);
        await Wait.Until(() => h.Email.Sent.Count == 1);
        await Task.Delay(100);
        Assert.Empty(h.WhatsApp.Sent);
        var payload = h.Email.Sent.Single();
        Assert.Equal("low", payload.Urgency);
        Assert.Contains("karena begitu", payload.Summary);
        var agent = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.Equal("ignore", agent.Action);
        Assert.Equal("karena begitu", agent.Reason);
    }

    [Fact]
    public async Task An_ignore_decision_with_only_whatsapp_has_no_quiet_channel_and_is_only_logged()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore", "low")), email: false);
        var job = h.Add();
        await Notify(h, job);
        await Task.Delay(150);
        Assert.Equal(0, h.Sent);
        Assert.Empty(h.WhatsApp.Sent);
        Assert.Equal("ignore", h.App.Store.Get(job.Id).Screening!.Agent!.Action);
    }

    [Fact]
    public async Task A_weapon_the_agent_tries_to_ignore_is_sent_as_critical_to_every_channel()
    {
        await using var h = new Harness(new FakeAgent(Says("ignore", "low")));
        var job = h.Add(suspicious: "no", weapon: "yes");
        await Notify(h, job);
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1 && h.Email.Sent.Count == 1);
        Assert.Equal("critical", h.WhatsApp.Sent.Single().Urgency);
        var agent = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.True(agent.GuardOverride);
    }

    // ---- the agent fails ----

    [Fact]
    public async Task When_the_agent_fails_the_alert_still_goes_out_and_the_failure_is_logged()
    {
        var agent = new FakeAgent(_ => throw new AgentException("The agent timed out."));
        await using var h = new Harness(agent);
        var job = h.Add();
        await Notify(h, job);
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1 && h.Email.Sent.Count == 1);
        Assert.Null(h.WhatsApp.Sent.Single().Urgency);   // legacy payload: no agent urgency/summary
        var log = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.True(log.FallbackUsed);
        Assert.Equal("none", log.Action);
        Assert.Equal("The agent timed out.", log.Error);
    }

    [Fact]
    public async Task A_long_agent_error_is_truncated_in_the_log()
    {
        await using var h = new Harness(new FakeAgent(_ => throw new AgentException(new string('x', 1000))));
        var job = h.Add();
        await Notify(h, job);
        Assert.Equal(300, h.App.Store.Get(job.Id).Screening!.Agent!.Error!.Length);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_into_a_fallback()
    {
        using var cts = new CancellationTokenSource();
        var agent = new FakeAgent(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        await using var h = new Harness(agent);
        var job = h.Add();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            h.Flags.NotifyAsync(h.App.Store.Get(job.Id), Local, (_, _) => Task.CompletedTask, cts.Token));
        await Task.Delay(100);
        Assert.Equal(0, h.Sent);
    }

    // ---- asking for more frames ----

    [Fact]
    public async Task More_frames_are_analysed_once_then_the_agent_decides_again_without_that_option()
    {
        var agent = new FakeAgent(Says("request_more_frames", "medium"), Says("send_whatsapp", "high"));
        await using var h = new Harness(agent);
        var job = h.Add();
        var reanalysed = 0;
        await Notify(h, job, reanalyze: (_, _) => { reanalysed++; return Task.CompletedTask; });
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1);
        Assert.Equal(1, reanalysed);
        Assert.Equal(2, agent.Calls);
        Assert.True(agent.Contexts[0].CanRequestMoreFrames);
        Assert.False(agent.Contexts[1].CanRequestMoreFrames);
        var log = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.Equal(1, log.ExtraFramePasses);
        Assert.Equal("send_whatsapp", log.Action);
    }

    [Fact]
    public async Task The_second_decision_sees_the_updated_job_from_the_extra_pass()
    {
        var agent = new FakeAgent(Says("request_more_frames"), Says("send_email", "low"));
        await using var h = new Harness(agent);
        var job = h.Add();
        await Notify(h, job, reanalyze: (id, _) =>
        {
            h.App.Store.Update(id, j => j with { Description = "Deskripsi baru setelah frame tambahan." });
            return Task.CompletedTask;
        });
        Assert.Equal("Deskripsi baru setelah frame tambahan.", agent.Contexts[1].Job.Description);
    }

    [Fact]
    public async Task If_the_extra_pass_clears_the_flag_nothing_is_sent()
    {
        var agent = new FakeAgent(Says("request_more_frames"));
        await using var h = new Harness(agent);
        var job = h.Add();
        await Notify(h, job, reanalyze: (id, _) =>
        {
            h.App.Store.Update(id, j => j with { Suspicious = "no" });
            return Task.CompletedTask;
        });
        await Task.Delay(150);
        Assert.Equal(1, agent.Calls);   // no second question
        Assert.Equal(0, h.Sent);
        var log = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.Contains("Tidak lagi ditandai", log.Reason);
        Assert.Equal(1, log.ExtraFramePasses);
    }

    [Fact]
    public async Task If_the_extra_pass_itself_fails_the_legacy_alert_is_sent_and_the_pass_is_counted()
    {
        var agent = new FakeAgent(Says("request_more_frames"));
        await using var h = new Harness(agent);
        var job = h.Add();
        await Notify(h, job, reanalyze: (_, _) => throw new UserError("Model server returned 500.", 502));
        await Wait.Until(() => h.WhatsApp.Sent.Count == 1 && h.Email.Sent.Count == 1);
        var log = h.App.Store.Get(job.Id).Screening!.Agent!;
        Assert.True(log.FallbackUsed);
        Assert.Equal(1, log.ExtraFramePasses);
    }
}

public class LlmNotificationAgentTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Completion(string content, string finish = "stop") =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = finish } } });

    private static AgentContext Ctx(VideoJob? job = null, ModelSettings? settings = null) => new(
        job ?? new VideoJob { Id = "j", Name = "v.mp4", CameraName = "gudang", Suspicious = "yes", Weapon = "no", Description = "Orang membuka pintu." },
        settings ?? new ModelSettings(), ["whatsapp", "email", "log"], true);

    private static async Task<AgentDecision> Decide(string answer, AgentContext? ctx = null, StubHandler? handler = null)
    {
        handler ??= new StubHandler(_ => Json(answer));
        return await new LlmNotificationAgent(new StubFactory(handler)).DecideAsync(ctx ?? Ctx(), CancellationToken.None);
    }

    [Fact]
    public async Task A_well_formed_answer_becomes_a_validated_decision()
    {
        var d = await Decide(Completion("""{"reason":"jelas","action":"send_whatsapp","urgency":"high","summary":"Kamera gudang: orang membuka pintu."}"""));
        Assert.Equal("send_whatsapp", d.Action);
        Assert.Equal("high", d.Urgency);
        Assert.Equal("Kamera gudang: orang membuka pintu.", d.Summary);
    }

    [Fact]
    public async Task JSON_wrapped_in_extra_text_is_still_read()
    {
        var d = await Decide(Completion("Berikut keputusan: {\"reason\":\"r\",\"action\":\"ignore\",\"urgency\":\"low\",\"summary\":\"\"} selesai"));
        Assert.Equal("ignore", d.Action);
    }

    [Fact]
    public async Task The_request_restricts_the_action_enum_to_what_is_allowed_and_asks_for_no_scores()
    {
        var handler = new StubHandler(_ => Json(Completion("""{"reason":"r","action":"ignore","urgency":"low","summary":""}""")));
        await Decide("", Ctx(), handler);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var actions = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "send_whatsapp", "send_email", "ignore", "request_more_frames" }, actions);
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
    }

    [Theory]
    [InlineData("""{"reason":"r","action":"send_sms","urgency":"high","summary":"x"}""")]        // action not allowed
    [InlineData("""{"reason":"r","action":"send_email","urgency":"asap","summary":"x"}""")]       // bad urgency
    [InlineData("""{"reason":"r","action":"send_email","urgency":"low","summary":""}""")]         // send without summary
    [InlineData("""{"reason":"r","urgency":"low","summary":"x"}""")]                              // action missing
    public async Task An_answer_that_breaks_the_rules_is_rejected(string content) =>
        await Assert.ThrowsAsync<AgentException>(() => Decide(Completion(content)));

    [Fact]
    public async Task An_empty_or_truncated_answer_is_rejected()
    {
        await Assert.ThrowsAsync<AgentException>(() => Decide(Completion("")));
        await Assert.ThrowsAsync<AgentException>(() =>
            Decide(Completion("""{"reason":"r","action":"ignore","urgency":"low","summary":""}""", finish: "length")));
    }

    [Fact]
    public async Task Text_that_is_not_json_is_rejected()
    {
        // ParseObject reports this as a UserError; FlagNotifier catches any Exception, so the fallback still applies.
        await Assert.ThrowsAsync<UserError>(() => Decide(Completion("saya tidak tahu")));
    }

    [Fact]
    public async Task A_model_server_error_surfaces_so_the_caller_can_fall_back()
    {
        var handler = new StubHandler(_ => Json("""{"error":"model not found"}""", HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<UserError>(() => Decide("", Ctx(), handler));
        Assert.Contains("500", ex.Message);
    }

    // ---- what the agent is told ----

    private static JsonElement Facts(AgentContext ctx)
    {
        var text = LlmNotificationAgent.Facts(ctx, AgentPolicy.AllowedActions(ctx));
        return JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]).RootElement;
    }

    [Fact]
    public void The_facts_carry_the_score_and_the_threshold_so_the_agent_can_judge_borderline_cases()
    {
        var job = new VideoJob
        {
            Id = "j", Name = "v.mp4", CameraName = "gudang", Suspicious = "yes", Weapon = "no", Description = "d",
            Timestamps = [1, 2, 3], Screening = new ScreeningLog { SuspiciousScore = 0.52 }
        };
        var f = Facts(Ctx(job, new ModelSettings { SuspiciousThreshold = 0.5 }));
        Assert.Equal(0.52, f.GetProperty("skor_suspicious").GetDouble());
        Assert.Equal(0.5, f.GetProperty("ambang_suspicious").GetDouble());
        Assert.Equal(3, f.GetProperty("jumlah_frame").GetInt32());
        Assert.Equal("gudang", f.GetProperty("kamera").GetString());
    }

    [Fact]
    public void A_disabled_weapon_check_is_not_reported_as_no()
    {
        var f = Facts(Ctx(new VideoJob { Id = "j", Name = "v", WeaponEnabled = false, Weapon = "no" }));
        Assert.Equal("dinonaktifkan", f.GetProperty("weapon").GetString());
    }

    [Fact]
    public void Site_context_and_custom_criteria_are_included_only_when_set_and_enabled()
    {
        var job = new VideoJob { Id = "j", Name = "v" };
        var off = Facts(Ctx(job, new ModelSettings { SiteContextEnabled = false, SiteContext = "Gudang beras", FlagActivity = "  " }));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("konteks_lokasi").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("kriteria_penandaan_khusus").ValueKind);

        var on = Facts(Ctx(job, new ModelSettings { SiteContextEnabled = true, SiteContext = " Gudang beras ", FlagActivity = " Orang tanpa rompi " }));
        Assert.Equal("Gudang beras", on.GetProperty("konteks_lokasi").GetString());
        Assert.Equal("Orang tanpa rompi", on.GetProperty("kriteria_penandaan_khusus").GetString());
    }
}
