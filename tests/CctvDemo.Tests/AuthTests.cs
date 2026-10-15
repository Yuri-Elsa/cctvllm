using Xunit;

public class AuthOptionsTests
{
    [Fact]
    public void Sign_in_is_off_unless_a_password_is_set()
    {
        using (new EnvScope(("CCTV_UI_PASSWORD", null))) Assert.False(new AuthOptions().Enabled);
        using (new EnvScope(("CCTV_UI_PASSWORD", ""))) Assert.False(new AuthOptions().Enabled);
        using (new EnvScope(("CCTV_UI_PASSWORD", "secret-123"))) Assert.True(new AuthOptions().Enabled);
    }

    [Fact]
    public void Only_the_exact_password_is_accepted()
    {
        using var env = new EnvScope(("CCTV_UI_PASSWORD", "Correct Horse 1"));
        var auth = new AuthOptions();
        Assert.True(auth.Check("Correct Horse 1"));
        Assert.False(auth.Check("correct horse 1"));
        Assert.False(auth.Check("Correct Horse 1 ")); 
        Assert.False(auth.Check(""));
        Assert.False(auth.Check(null));
    }

    [Fact]
    public void Nothing_is_accepted_when_no_password_is_set()
    {
        using var env = new EnvScope(("CCTV_UI_PASSWORD", null));
        Assert.False(new AuthOptions().Check(""));
        Assert.False(new AuthOptions().Check("anything"));
    }

    [Fact]
    public void The_cookie_version_changes_with_the_password()
    {
        string a, b, a2;
        using (new EnvScope(("CCTV_UI_PASSWORD", "first-password"))) a = new AuthOptions().Version;
        using (new EnvScope(("CCTV_UI_PASSWORD", "second-password"))) b = new AuthOptions().Version;
        using (new EnvScope(("CCTV_UI_PASSWORD", "first-password"))) a2 = new AuthOptions().Version;
        Assert.NotEqual(a, b);
        Assert.Equal(a, a2);
        Assert.DoesNotContain("first-password", a);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://127.0.0.1:8766", true)]
    [InlineData("http://localhost:8766", true)]
    [InlineData("http://[::1]:8766", true)]
    [InlineData("http://127.0.0.1:8766;http://localhost:9000", true)]
    [InlineData("http://0.0.0.0:8766", false)]
    [InlineData("http://+:8766", false)]
    [InlineData("http://*:8766", false)]
    [InlineData("http://192.168.1.20:8766", false)]
    [InlineData("http://127.0.0.1:8766;http://0.0.0.0:9000", false)]
    public void Loopback_detection_for_CCTV_URL(string? urls, bool expected) =>
        Assert.Equal(expected, AuthOptions.IsLoopbackOnly(urls));
}

public class LoginThrottleTests
{
    [Fact]
    public void An_address_is_blocked_after_too_many_wrong_attempts()
    {
        var throttle = new LoginThrottle(maxFailures: 3);
        for (var i = 0; i < 2; i++) { throttle.Fail("a"); Assert.False(throttle.IsBlocked("a")); }
        throttle.Fail("a");
        Assert.True(throttle.IsBlocked("a"));
        Assert.False(throttle.IsBlocked("b")); 
    }

    [Fact]
    public void The_block_ends_after_the_window()
    {
        var now = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        var throttle = new LoginThrottle(maxFailures: 2, window: TimeSpan.FromMinutes(5)) { Now = () => now };
        throttle.Fail("a"); throttle.Fail("a");
        Assert.True(throttle.IsBlocked("a"));

        now = now.AddMinutes(4);
        Assert.True(throttle.IsBlocked("a"));
        now = now.AddMinutes(2);
        Assert.False(throttle.IsBlocked("a"));
    }

    [Fact]
    public void A_correct_sign_in_clears_the_count()
    {
        var throttle = new LoginThrottle(maxFailures: 2);
        throttle.Fail("a"); throttle.Fail("a");
        throttle.Reset("a");
        Assert.False(throttle.IsBlocked("a"));
    }
}

public class PreflightAccessTests
{
    [Fact]
    public void Open_access_on_loopback_is_fine() =>
        Assert.Equal("ok", Preflight.AccessCheck(null, null).Status);

    [Fact]
    public void Open_access_on_a_network_address_is_a_warning()
    {
        var check = Preflight.AccessCheck(null, "http://0.0.0.0:8766");
        Assert.Equal("warn", check.Status);
        Assert.Contains("CCTV_UI_PASSWORD", check.Detail);
    }

    [Fact]
    public void A_short_password_is_a_warning() =>
        Assert.Equal("warn", Preflight.AccessCheck("abc", null).Status);

    [Fact]
    public void A_password_over_plain_http_on_the_network_is_a_warning() =>
        Assert.Equal("warn", Preflight.AccessCheck("long-enough-password", "http://0.0.0.0:8766").Status);

    [Theory]
    [InlineData("long-enough-password", null)]
    [InlineData("long-enough-password", "https://0.0.0.0:8766")]
    public void A_good_password_is_ok(string password, string? urls) =>
        Assert.Equal("ok", Preflight.AccessCheck(password, urls).Status);
}