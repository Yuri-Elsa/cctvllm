using Xunit;

public class UserStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cctv-users-" + Guid.NewGuid().ToString("N"));

    public UserStoreTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Write(string json) { var path = Path.Combine(dir, "users.json"); File.WriteAllText(path, json); return path; }

    [Fact]
    public void A_missing_file_means_no_accounts() => Assert.False(new UserStore(Path.Combine(dir, "none.json")).Enabled);

    [Fact]
    public void The_right_password_signs_in_with_the_stored_role()
    {
        var hash = UserStore.Hash("correct horse battery");
        var store = new UserStore(Write($$"""[{"name":"Ani","role":"reviewer","hash":"{{hash}}"}]"""));
        Assert.True(store.Enabled);
        var account = store.Check("ani", "correct horse battery");   // names are not case sensitive
        Assert.Equal("reviewer", account?.Role);
        Assert.Null(store.Check("ani", "wrong"));
        Assert.Null(store.Check("nobody", "correct horse battery"));
        Assert.Null(store.Check("ani", null));
    }

    [Fact]
    public void A_hash_never_contains_the_password_and_differs_each_time()
    {
        var a = UserStore.Hash("same-password"); var b = UserStore.Hash("same-password");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("same-password", a);
        Assert.True(UserStore.Verify(a, "same-password"));
        Assert.False(UserStore.Verify("not-a-hash", "same-password"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""[{"name":"a","role":"boss","hash":"pbkdf2-sha256$1$AA==$AA=="}]""")]
    [InlineData("""[{"name":"a","role":"admin","hash":"plain-text"}]""")]
    public void A_broken_file_stops_the_app_instead_of_opening_it(string json) =>
        Assert.Throws<InvalidOperationException>(() => new UserStore(Write(json)));
}

public class SessionStoreTests
{
    [Fact]
    public void A_revoked_session_is_no_longer_active()
    {
        var sessions = new SessionStore();
        var a = sessions.Start("ani", "viewer", "1.1.1.1"); var b = sessions.Start("budi", "admin", null);
        Assert.True(sessions.IsActive(a.Id));
        Assert.True(sessions.Revoke(a.Id));
        Assert.False(sessions.IsActive(a.Id));
        Assert.True(sessions.IsActive(b.Id));
        Assert.False(sessions.IsActive(null));
        Assert.False(sessions.IsActive("unknown"));
    }

    [Fact]
    public void Revoke_all_keeps_the_callers_own_session()
    {
        var sessions = new SessionStore();
        var mine = sessions.Start("admin", "admin", null);
        sessions.Start("a", "viewer", null); sessions.Start("b", "viewer", null);
        Assert.Equal(2, sessions.RevokeAll(mine.Id));
        Assert.Equal(1, sessions.List().Count);
        Assert.True(sessions.IsActive(mine.Id));
    }

    [Fact]
    public void An_idle_session_expires()
    {
        var now = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
        var sessions = new SessionStore(TimeSpan.FromHours(12)) { Now = () => now };
        var s = sessions.Start("ani", "viewer", null);
        now = now.AddHours(11); Assert.True(sessions.IsActive(s.Id));    // use pushes the limit forward
        now = now.AddHours(11); Assert.True(sessions.IsActive(s.Id));
        now = now.AddHours(13); Assert.False(sessions.IsActive(s.Id));
    }
}

public class AuditLogTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cctv-audit-" + Guid.NewGuid().ToString("N"));

    public AuditLogTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Entries_come_back_newest_first_with_who_and_what()
    {
        var log = new AuditLog(dir);
        log.Record("ani", "10.0.0.5", "review_set", "job1", "none -> confirmed");
        log.Record("budi", null, "settings_changed", null, "Model=x");
        var entries = log.Recent(10);
        Assert.Equal(new[] { "settings_changed", "review_set" }, entries.Select(e => e.Action).ToArray());
        Assert.Equal("ani", entries[1].Actor);
        Assert.Equal("none -> confirmed", entries[1].Detail);
    }

    [Fact]
    public void A_newline_in_a_value_cannot_forge_a_second_entry()
    {
        var log = new AuditLog(dir);
        log.Record("evil\n{\"actor\":\"admin\"}", null, "login_failed");
        Assert.Single(File.ReadAllLines(Path.Combine(dir, "audit.jsonl")));
        Assert.Single(log.Recent(10));
    }

    [Fact]
    public void Long_values_are_cut() =>
        Assert.True(new AuditLog(dir) is var log && Record(log).Detail!.Length < 600);

    private AuditEntry Record(AuditLog log) { log.Record("a", null, "x", null, new string('z', 5000)); return log.Recent(1)[0]; }
}

public class MetricsTests
{
    private static VideoJob Job(string status, string suspicious = "no") => new() { Id = Guid.NewGuid().ToString("N"), Status = status, Suspicious = suspicious };

    [Fact]
    public void Queue_and_camera_numbers_are_rendered_in_prometheus_format()
    {
        var jobs = new[] { Job("queued"), Job("queued"), Job("completed", "yes"), Job("failed") };
        var cameras = new[] { new CameraStatus("Gate \"A\"", "running", null, 0, null), new CameraStatus("Yard", "retrying", null, 2, "x") };
        var text = Metrics.Render(jobs, true, cameras, 3, DateTimeOffset.UtcNow);
        Assert.Contains("cctv_jobs{status=\"queued\"} 2\n", text);
        Assert.Contains("cctv_jobs{status=\"failed\"} 1\n", text);
        Assert.Contains("cctv_jobs_flagged 1\n", text);
        Assert.Contains("cctv_queue_running 1\n", text);
        Assert.Contains("cctv_alerts_pending 3\n", text);
        Assert.Contains("cctv_camera_up{camera=\"Gate \\\"A\\\"\"} 1\n", text);
        Assert.Contains("cctv_camera_up{camera=\"Yard\"} 0\n", text);
    }

    [Fact]
    public void Model_latency_is_a_histogram_whose_buckets_only_grow()
    {
        Metrics.ModelCall(TimeSpan.FromSeconds(0.3), true);
        Metrics.ModelCall(TimeSpan.FromSeconds(7), false);
        var text = Metrics.Render([], false, [], 0, DateTimeOffset.UtcNow);
        var counts = text.Split('\n').Where(l => l.StartsWith("cctv_model_request_duration_seconds_bucket")).Select(l => long.Parse(l[(l.LastIndexOf(' ') + 1)..])).ToList();
        Assert.Equal(counts.OrderBy(c => c), counts);
        Assert.Contains("cctv_model_request_duration_seconds_count ", text);
        Assert.Contains("cctv_model_requests_failed_total ", text);
    }
}

public class HealthTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_model_alert_waits_for_two_failed_checks_then_reminds_then_recovers()
    {
        using var env = new EnvScope(("CCTV_MODEL_DOWN_CHECKS", "2"), ("CCTV_HEALTH_REMINDER_MINUTES", "30"), ("CCTV_BACKLOG_ALERT", "0"));
        var tracker = new HealthTracker(new HealthOptions());
        Assert.Empty(tracker.Step(T0, false, 0, true, "http://x/v1"));
        Assert.Equal(AlertKind.ModelDown, Assert.Single(tracker.Step(T0.AddSeconds(30), false, 0, true, "http://x/v1")).Kind);
        Assert.Empty(tracker.Step(T0.AddMinutes(5), false, 0, true, "http://x/v1"));
        Assert.Equal(AlertKind.ModelDown, Assert.Single(tracker.Step(T0.AddMinutes(31), false, 0, true, "http://x/v1")).Kind);
        Assert.Equal(AlertKind.ModelRecovered, Assert.Single(tracker.Step(T0.AddMinutes(32), true, 0, true, "http://x/v1")).Kind);
        Assert.Empty(tracker.Step(T0.AddMinutes(33), true, 0, true, "http://x/v1"));
    }

    [Fact]
    public void One_failed_check_is_not_an_alert()
    {
        using var env = new EnvScope(("CCTV_MODEL_DOWN_CHECKS", "2"), ("CCTV_BACKLOG_ALERT", "0"));
        var tracker = new HealthTracker(new HealthOptions());
        Assert.Empty(tracker.Step(T0, false, 0, true, "e"));
        Assert.Empty(tracker.Step(T0.AddSeconds(30), true, 0, true, "e"));
        Assert.Empty(tracker.Step(T0.AddSeconds(60), false, 0, true, "e"));
    }

    [Fact]
    public void A_backlog_must_last_before_it_is_reported_and_clears_only_below_half()
    {
        using var env = new EnvScope(("CCTV_BACKLOG_ALERT", "10"), ("CCTV_BACKLOG_MINUTES", "5"), ("CCTV_HEALTH_REMINDER_MINUTES", "0"));
        var tracker = new HealthTracker(new HealthOptions());
        Assert.Empty(tracker.Step(T0, true, 30, true, "e"));
        Assert.Empty(tracker.Step(T0.AddMinutes(4), true, 30, true, "e"));
        Assert.Equal(AlertKind.QueueBacklog, Assert.Single(tracker.Step(T0.AddMinutes(5), true, 30, true, "e")).Kind);
        Assert.Empty(tracker.Step(T0.AddMinutes(20), true, 30, true, "e"));          // reminder is off
        Assert.Empty(tracker.Step(T0.AddMinutes(21), true, 8, true, "e"));           // below the limit but not below half
        Assert.Equal(AlertKind.QueueRecovered, Assert.Single(tracker.Step(T0.AddMinutes(22), true, 5, true, "e")).Kind);
    }

    [Fact]
    public void A_short_spike_is_forgotten()
    {
        using var env = new EnvScope(("CCTV_BACKLOG_ALERT", "10"), ("CCTV_BACKLOG_MINUTES", "5"));
        var tracker = new HealthTracker(new HealthOptions());
        tracker.Step(T0, true, 30, true, "e");
        tracker.Step(T0.AddMinutes(2), true, 1, true, "e");
        Assert.Empty(tracker.Step(T0.AddMinutes(6), true, 30, true, "e"));           // the clock started again at minute 6
    }
}