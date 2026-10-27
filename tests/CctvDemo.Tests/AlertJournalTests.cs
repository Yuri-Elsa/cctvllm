using Xunit;

public class AlertJournalTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cctv-journal-" + Guid.NewGuid().ToString("N"));
    private string JournalFile => Path.Combine(dir, "pending-alerts.json");

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static NotificationPayload Alert(string job, string key) =>
        new(job, "lobby", DateTimeOffset.UtcNow, "orang di pintu", true, false, "http://x") { Key = key };

    [Fact]
    public void Waiting_alerts_come_back_after_a_restart()
    {
        var options = new NotificationOptions();
        new AlertJournal(JournalFile, options).Add(Alert("job1", "k1"));

        var restarted = new AlertJournal(JournalFile, options);

        var recovered = Assert.Single(restarted.Recovered);
        Assert.Equal("job1", recovered.JobId);
        Assert.Equal("k1", recovered.Key);
    }

    [Fact]
    public void A_delivered_alert_is_not_replayed()
    {
        var options = new NotificationOptions();
        var journal = new AlertJournal(JournalFile, options);
        journal.Add(Alert("job1", "k1"));
        journal.Complete(["k1"]);

        var restarted = new AlertJournal(JournalFile, options);
        Assert.Empty(restarted.Recovered);
        Assert.Equal(0, restarted.Count);
    }

    [Fact]
    public void A_failed_channel_is_kept_for_a_retry_and_the_pause_grows_up_to_a_limit()
    {
        var options = new NotificationOptions { RetryBase = TimeSpan.FromSeconds(10), RetryMax = TimeSpan.FromSeconds(25) };
        var journal = new AlertJournal(null, options);
        var alert = Alert("job1", "k1");
        journal.Add(alert);

        Assert.True(journal.Fail(alert with { Channels = ["email"] }, [alert], null));
        var first = Assert.Single(journal.DueRetries(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(1, first.Attempts);
        Assert.Equal(new[] { "email" }, first.Message.Channels);
        Assert.Empty(journal.DueRetries(DateTimeOffset.UtcNow));            // 10 s pause: not due yet

        Assert.True(journal.Fail(first.Message, [alert], first.Key));       // attempt 2: 20 s
        Assert.True(journal.Fail(first.Message, [alert], first.Key));       // attempt 3: 40 s, capped at 25 s
        Assert.Empty(journal.DueRetries(DateTimeOffset.UtcNow.AddSeconds(24)));
        var last = Assert.Single(journal.DueRetries(DateTimeOffset.UtcNow.AddSeconds(26)));
        Assert.Equal(3, last.Attempts);
        Assert.Equal(first.Key, last.Key);
        Assert.Equal(1, journal.Count);                                      // replaced, not duplicated
    }

    [Fact]
    public void A_retry_entry_survives_a_restart_but_is_not_replayed_as_a_new_alert()
    {
        var options = new NotificationOptions();
        var journal = new AlertJournal(JournalFile, options);
        var alert = Alert("job1", "k1");
        journal.Add(alert);
        journal.Fail(alert with { Channels = ["whatsapp"] }, [alert], null);

        var restarted = new AlertJournal(JournalFile, options);
        Assert.Empty(restarted.Recovered);
        var entry = Assert.Single(restarted.DueRetries(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(new[] { "whatsapp" }, entry.Message.Channels);
        Assert.Equal(new[] { "job1" }, entry.CoveredJobIds);
    }

    [Fact]
    public void Retrying_stops_once_the_window_has_passed()
    {
        var options = new NotificationOptions { RetryWindow = TimeSpan.FromMilliseconds(50) };
        var journal = new AlertJournal(null, options);
        var alert = Alert("job1", "k1");
        journal.Add(alert);
        Thread.Sleep(150);

        Assert.False(journal.Fail(alert, [alert], null));
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void Alerts_older_than_the_window_are_dropped_on_load()
    {
        var options = new NotificationOptions { RetryWindow = TimeSpan.FromMilliseconds(20) };
        new AlertJournal(JournalFile, options).Add(Alert("job1", "k1"));
        Thread.Sleep(100);

        var restarted = new AlertJournal(JournalFile, options);
        Assert.Empty(restarted.Recovered);
        Assert.Equal(0, restarted.Count);
    }

    [Fact]
    public void A_damaged_journal_does_not_stop_startup_and_is_kept_for_inspection()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(JournalFile, "[ this is not json");

        var journal = new AlertJournal(JournalFile, new NotificationOptions());

        Assert.Equal(0, journal.Count);
        Assert.Single(Directory.GetFiles(dir, "pending-alerts.json.corrupt-*"));
    }
}
