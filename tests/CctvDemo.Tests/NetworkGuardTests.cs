using Xunit;

public class HostGuardTests
{
    [Theory]
    [InlineData("localhost:8766", "localhost")]
    [InlineData("127.0.0.1:8766", "127.0.0.1")]
    [InlineData("[::1]:8766", "::1")]
    [InlineData("cctv.example.com", "cctv.example.com")]
    public void Host_header_is_split_from_its_port(string header, string expected) =>
        Assert.Equal(expected, HostGuard.HostOnly(header));

    [Theory]
    [InlineData("http://127.0.0.1:8766", null, "localhost:8766", true)]
    [InlineData("http://127.0.0.1:8766", null, "127.0.0.1:8766", true)]
    [InlineData("http://127.0.0.1:8766", null, "[::1]:8766", true)]
    [InlineData("http://127.0.0.1:8766", null, "evil.example:8766", false)]   // DNS rebinding
    [InlineData("http://127.0.0.1:8766", null, null, false)]
    [InlineData("http://127.0.0.1:8766", "cctv.example.com", "cctv.example.com", true)]
    [InlineData("http://127.0.0.1:8766", "cctv.example.com", "evil.example", false)]
    [InlineData("http://0.0.0.0:8766", null, "anything.example", true)]       // no restriction when exposed without a list
    public void Host_header_is_checked(string urls, string? configured, string? header, bool expected) =>
        Assert.Equal(expected, HostGuard.IsAllowed(header, HostGuard.Allowed(urls, configured)));

    [Theory]
    [InlineData("http://localhost:8766", "localhost:8766", true)]
    [InlineData("https://cctv.example.com", "cctv.example.com", true)]       // behind a TLS proxy
    [InlineData("https://evil.example", "cctv.example.com", false)]
    [InlineData("http://localhost:9999", "localhost:8766", false)]
    [InlineData("null", "localhost:8766", false)]
    [InlineData("", "localhost:8766", false)]
    public void Origin_must_match_the_host(string origin, string host, bool expected) =>
        Assert.Equal(expected, HostGuard.SameOrigin(origin, host));
}

public class ModelEndpointPolicyTests
{
    [Theory]
    [InlineData("local", "http://127.0.0.1:11434/v1", true)]
    [InlineData("local", "http://localhost:11434/v1", true)]
    [InlineData("local", "http://example.com/v1", false)]
    [InlineData("notebook", "http://127.0.0.1:8000", true)]
    [InlineData("notebook", "https://169.254.169.254", false)]
    [InlineData("notebook", "https://user:pw@127.0.0.1", false)]
    [InlineData("notebook", "ftp://127.0.0.1", false)]
    public void Without_an_allow_list_only_loopback_is_accepted(string mode, string endpoint, bool ok)
    {
        using var env = new EnvScope(("CCTV_NOTEBOOK_HOSTS", null), ("CCTV_NOTEBOOK_ALLOW_HTTP", null));
        Assert.Equal(ok, ModelEndpointPolicy.Check(mode, endpoint) is null);
    }

    [Fact]
    public void A_listed_notebook_host_needs_https_unless_allowed()
    {
        using (new EnvScope(("CCTV_NOTEBOOK_HOSTS", "gpu.example.com"), ("CCTV_NOTEBOOK_ALLOW_HTTP", null)))
        {
            Assert.Null(ModelEndpointPolicy.Check("notebook", "https://gpu.example.com"));
            Assert.NotNull(ModelEndpointPolicy.Check("notebook", "http://gpu.example.com"));
            Assert.NotNull(ModelEndpointPolicy.Check("notebook", "https://other.example.com"));
            Assert.NotNull(ModelEndpointPolicy.Check("local", "https://gpu.example.com"));
        }
        using (new EnvScope(("CCTV_NOTEBOOK_HOSTS", "gpu.example.com"), ("CCTV_NOTEBOOK_ALLOW_HTTP", "1")))
            Assert.Null(ModelEndpointPolicy.Check("notebook", "http://gpu.example.com"));
    }

    [Fact]
    public void Ensure_throws_for_a_refused_endpoint()
    {
        using var env = new EnvScope(("CCTV_NOTEBOOK_HOSTS", null));
        Assert.Throws<UserError>(() => ModelEndpointPolicy.Ensure(new ModelSettings(Mode: "notebook", Endpoint: "https://evil.example")));
        ModelEndpointPolicy.Ensure(new ModelSettings());
    }
}
