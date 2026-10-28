using System.Net;

// Host header check. Without it a web page can use DNS rebinding to reach an app that only
// listens on 127.0.0.1: the browser then sends "Origin: http://evil.example" and
// "Host: evil.example", which the same-origin check alone would accept.
public static class HostGuard
{
    // Names allowed in the Host header, or null for "no restriction".
    //   CCTV_ALLOWED_HOSTS=cctv.example.com,192.168.1.20   (comma separated, no ports)
    // Without it, an app bound to loopback only accepts localhost / loopback addresses.
    public static HashSet<string>? Allowed(string? urls, string? configured)
    {
        var names = (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant()).ToHashSet();
        if (names.Count > 0) { names.Add("localhost"); return names; }
        if (!AuthOptions.IsLoopbackOnly(urls)) return null;
        return new HashSet<string> { "localhost" };
    }

    // "localhost:8766" -> "localhost", "[::1]:8766" -> "::1".
    public static string HostOnly(string hostHeader)
    {
        var host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            return end > 0 ? host[1..end] : host;
        }
        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(':') == colon ? host[..colon] : host;
    }

    public static bool IsAllowed(string? hostHeader, HashSet<string>? allowed)
    {
        if (allowed is null) return true;
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        var host = HostOnly(hostHeader).TrimEnd('.').ToLowerInvariant();
        if (allowed.Contains(host)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    // Compares host:port only, not the scheme: behind a TLS-terminating proxy Kestrel sees "http"
    // while the browser's Origin says "https".
    public static bool SameOrigin(string? origin, string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        return string.Equals(uri.Authority, hostHeader.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

// Decides where the app may send video frames and CCTV_API_KEY: loopback addresses only (the local Ollama server).
public static class ModelEndpointPolicy
{
    public static string? Check(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return "Enter a valid server URL without embedded credentials.";
        return uri.IsLoopback ? null : "Local mode requires a localhost address.";
    }

    // Called right before anything is sent, so a bad endpoint saved earlier in state.json is refused too.
    public static void Ensure(ModelSettings config)
    {
        if (Check(config.Endpoint) is { } problem) throw new UserError(problem, 400);
    }
}