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

// Decides where the app may send video frames, whole videos and CCTV_API_KEY.
//   local mode:    loopback only.
//   notebook mode: loopback, or a host listed in CCTV_NOTEBOOK_HOSTS (comma separated), and HTTPS
//                  unless CCTV_NOTEBOOK_ALLOW_HTTP=1.
public static class ModelEndpointPolicy
{
    public static string? Check(string mode, string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return "Enter a valid server URL without embedded credentials.";
        if (uri.IsLoopback) return null;
        if (mode != "notebook") return "Local mode requires a localhost address.";
        var allowed = (Environment.GetEnvironmentVariable("CCTV_NOTEBOOK_HOSTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return $"Notebook host '{uri.Host}' is not allowed. Add it to CCTV_NOTEBOOK_HOSTS (comma separated) and restart.";
        if (uri.Scheme != "https" && Environment.GetEnvironmentVariable("CCTV_NOTEBOOK_ALLOW_HTTP") != "1")
            return "A remote notebook must use https (or set CCTV_NOTEBOOK_ALLOW_HTTP=1 for a trusted LAN).";
        return null;
    }

    // Called right before anything is sent, so a bad endpoint saved earlier in state.json is refused too.
    public static void Ensure(ModelSettings config)
    {
        if (Check(config.Mode, config.Endpoint) is { } problem) throw new UserError(problem, 400);
    }
}
