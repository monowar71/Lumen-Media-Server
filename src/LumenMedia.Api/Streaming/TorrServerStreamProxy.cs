using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace LumenMedia.Api.Streaming;

/// <summary>
/// Proxies TorrServer <c>/play/…</c> for DirectPlay with HTTP Range forwarding
/// (TorrServer stays on localhost).
/// </summary>
public interface ITorrServerStreamProxy
{
    bool IsAllowedPlayUrl(string? url);

    Task ProxyAsync(
        HttpRequest incoming,
        HttpResponse outgoing,
        string playUrl,
        string contentType,
        CancellationToken ct);
}

public sealed class TorrServerStreamProxy(
    IHttpClientFactory httpClientFactory,
    IOptions<TorrServerOptions> options,
    ILogger<TorrServerStreamProxy> logger) : ITorrServerStreamProxy
{
    public const string HttpClientName = "TorrServerPlay";

    public bool IsAllowedPlayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var baseUrl = options.Value.ResolveBaseUrl().TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var allowedBase))
            return false;

        if (!string.Equals(uri.Host, allowedBase.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        if (uri.Port != allowedBase.Port)
            return false;
        return uri.AbsolutePath.StartsWith("/play/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ProxyAsync(
        HttpRequest incoming,
        HttpResponse outgoing,
        string playUrl,
        string contentType,
        CancellationToken ct)
    {
        if (!IsAllowedPlayUrl(playUrl))
            throw new InvalidOperationException("Refusing to proxy non-TorrServer play URL.");

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, playUrl);
        if (incoming.Headers.TryGetValue("Range", out var rangeValues))
        {
            var range = rangeValues.ToString();
            if (!string.IsNullOrWhiteSpace(range))
                upstreamRequest.Headers.TryAddWithoutValidation("Range", range);
        }

        using var upstream = await client.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        outgoing.StatusCode = (int)upstream.StatusCode;
        outgoing.Headers.CacheControl = "no-store";
        outgoing.Headers["Accept-Ranges"] = "bytes";

        if (upstream.Content.Headers.ContentType is not null)
            outgoing.ContentType = upstream.Content.Headers.ContentType.ToString();
        else
            outgoing.ContentType = contentType;

        if (upstream.Content.Headers.ContentLength is long len)
            outgoing.ContentLength = len;

        if (upstream.Content.Headers.ContentRange is { } contentRange)
            outgoing.Headers["Content-Range"] = contentRange.ToString();

        try
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await body.CopyToAsync(outgoing.Body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected / seek aborted — expected.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TorrServer DirectPlay proxy stream ended for {Url}", playUrl);
            throw;
        }
    }
}
