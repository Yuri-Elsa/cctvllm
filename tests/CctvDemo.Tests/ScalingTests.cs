using Xunit;

public class ModelEndpointTests
{
    [Fact]
    public void Endpoint_lists_are_split_deduplicated_and_trimmed()
    {
        var list = ModelEndpoints.Parse("http://127.0.0.1:11434/v1/", "http://127.0.0.1:11434/v1\nhttps://gpu2.lan/v1, https://gpu3.lan/v1");
        Assert.True(list.SequenceEqual(new[] { "http://127.0.0.1:11434/v1", "https://gpu2.lan/v1", "https://gpu3.lan/v1" }));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", null, true)]
    [InlineData("http://gpu2.example.com:11434/v1", null, false)]                 // other machines need the allow list
    [InlineData("https://gpu2.example.com/v1", "gpu2.example.com", true)]
    [InlineData("http://gpu2.example.com:11434/v1", "gpu2.example.com", false)]   // ...and https, unless the address is private
    [InlineData("http://10.0.0.7:11434/v1", "10.0.0.7", true)]
    [InlineData("http://169.254.169.254/v1", "169.254.169.254", false)]           // link-local (cloud metadata) never
    [InlineData("http://10.0.0.8:11434/v1", "10.0.0.7", false)]                   // not on the list
    public void Model_hosts_are_checked(string endpoint, string? allow, bool ok) =>
        Assert.Equal(ok, ModelEndpointPolicy.Check(endpoint, allow) is null);

    [Theory]
    [InlineData("https://cctv.example.com", true)]
    [InlineData("http://192.168.1.5:8766", true)]
    [InlineData("http://cctv.example.com", false)]       // the token would travel in clear text over the internet
    [InlineData("ftp://192.168.1.5", false)]
    public void Coordinator_url_is_checked(string url, bool ok) =>
        Assert.Equal(ok, RemoteHost.CheckCoordinator(url) is null);
}
