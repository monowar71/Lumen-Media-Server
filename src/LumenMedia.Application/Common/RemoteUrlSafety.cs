using System.Net;
using System.Net.Sockets;

namespace LumenMedia.Application.Common;

/// <summary>Host allowlists and basic SSRF guards for server-side HTTP fetches.</summary>
public static class RemoteUrlSafety
{
    /// <summary>Hosts permitted for remote artwork downloads (TMDB / TVDB / TVMaze).</summary>
    public static readonly HashSet<string> ArtworkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.tmdb.org",
        "artworks.thetvdb.com",
        "static.tvmaze.com",
    };

    /// <summary>
    /// Validates an absolute HTTPS URL against an allowlist (no private-IP resolution —
    /// host must already be a known CDN).
    /// </summary>
    public static string EnsureAllowedHttpsHost(string url, IReadOnlySet<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !allowedHosts.Contains(uri.Host))
        {
            throw new ValidationException("url", "URL host is not allowed.");
        }

        return uri.ToString();
    }

    /// <summary>
    /// Validates outbound http(s) for admin-initiated integrations (e.g. Plex import).
    /// Blocks cloud-metadata / link-local / loopback; RFC1918 is allowed so LAN Plex works.
    /// </summary>
    public static Uri EnsureSafeIntegrationUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ValidationException("baseUrl", "URL must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new ValidationException("baseUrl", "URL host is required.");

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("baseUrl", "URL host is not allowed.");
        }

        if (IPAddress.TryParse(uri.Host, out var ip) && IsBlockedDestination(ip))
            throw new ValidationException("baseUrl", "URL host is not allowed.");

        // Hostnames: resolve and reject if any address is a blocked destination.
        // Failure to resolve is left to the HTTP client (do not soft-allow).
        if (!IPAddress.TryParse(uri.Host, out _))
        {
            try
            {
                var addresses = Dns.GetHostAddresses(uri.Host);
                if (addresses.Any(IsBlockedDestination))
                    throw new ValidationException("baseUrl", "URL host is not allowed.");
            }
            catch (SocketException)
            {
                // DNS failure — let the HTTP call fail naturally; do not treat as allow.
            }
            catch (ArgumentException)
            {
                throw new ValidationException("baseUrl", "URL host is not allowed.");
            }
        }

        return uri;
    }

    /// <summary>
    /// Cloud metadata, loopback, and link-local — never a legitimate Plex target for import.
    /// RFC1918 (LAN) is intentionally allowed for self-hosted Plex.
    /// </summary>
    public static bool IsBlockedDestination(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 169.254.0.0/16 link-local + cloud metadata (169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            // 0.0.0.0/8
            if (bytes[0] == 0)
                return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
            if (ip.Equals(IPAddress.IPv6Any))
                return true;
        }

        return false;
    }
}
