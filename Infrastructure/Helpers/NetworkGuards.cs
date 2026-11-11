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

// Which machines may receive video frames and CCTV_API_KEY. Loopback (the local Ollama) is always fine. A model server on
// another machine has to be named in CCTV_MODEL_HOSTS (comma separated: "gpu1.internal", "10.0.0.7:11434"; a name without a
// port allows any port) and has to be reached safely: over https, or by a private IP address (10.x, 172.16-31.x, 192.168.x,
// 100.64-127.x as used by Tailscale, or an IPv6 unique-local address). Plain http to a host name is refused because
// nothing says the path to it is private.
public static class ModelEndpointPolicy
{
    public static string? Check(string? endpoint) => Check(endpoint, Environment.GetEnvironmentVariable("CCTV_MODEL_HOSTS"));

    public static string? Check(string? endpoint, string? allowList)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return "Enter a valid server URL without embedded credentials.";
        if (uri.IsLoopback) return null;
        if (!Listed(uri, allowList))
            return "Local mode requires a localhost address. To use a model server on another machine, name its host in CCTV_MODEL_HOSTS.";
        if (uri.Scheme != "https" && !RemoteHost.IsPrivateAddress(uri.Host))
            return "A model server on another machine must use https or a private IP address (10.x, 172.16-31.x, 192.168.x, 100.64-127.x), " +
                   "because the frames and the API key travel to it.";
        return null;
    }

    private static bool Listed(Uri uri, string? allowList)
    {
        foreach (var entry in (allowList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = HostGuard.HostOnly(entry);
            if (!string.Equals(host, uri.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) continue;
            // "host:port" pins the port. IPv6 literals in brackets carry their port after the closing bracket.
            var tail = entry.StartsWith('[') ? entry[(entry.IndexOf(']') + 1)..] : entry.Length > host.Length ? entry[host.Length..] : "";
            if (tail.Length == 0 || (tail.StartsWith(':') && int.TryParse(tail[1..], out var port) && port == uri.Port)) return true;
        }
        return false;
    }

    // Called right before anything is sent, so a bad endpoint saved earlier in state.json is refused too.
    public static void Ensure(ModelSettings config)
    {
        foreach (var endpoint in ModelEndpoints.All(config))
            if (Check(endpoint) is { } problem) throw new UserError(endpoint + ": " + problem, 400);
    }
}

public static class RemoteHost
{
    // Private, shared (carrier-grade NAT, used by Tailscale) or unique-local addresses: reachable on a private network, not the open internet.
    // Link-local (169.254.x.x) is left out on purpose: it is where cloud metadata services live.
    public static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        return (b[0] & 0xFE) == 0xFC;
    }

    // Where a worker may talk to its coordinator: https, a private or loopback address, or CCTV_WORKER_ALLOW_HTTP=1.
    public static string? CheckCoordinator(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            return "CCTV_COORDINATOR_URL must be an http(s) URL without credentials.";
        if (uri.Scheme == "https" || uri.IsLoopback || IsPrivateAddress(uri.Host) ||
            Environment.GetEnvironmentVariable("CCTV_WORKER_ALLOW_HTTP") is "1" or "true") return null;
        return "CCTV_COORDINATOR_URL must use https or a private IP address, because the worker token and the videos travel over it. " +
               "Set CCTV_WORKER_ALLOW_HTTP=1 only if the network path is private.";
    }
}
