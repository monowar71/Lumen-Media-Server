using FluentAssertions;
using LumenMedia.Api.Streaming;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LumenMedia.Api.IntegrationTests;

public class TorrServerStreamProxyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8090/play/abc/1", true)]
    [InlineData("http://127.0.0.1:8090/torrent/list", false)]
    [InlineData("http://evil.example/play/abc/1", false)]
    [InlineData("/media/file.mkv", false)]
    [InlineData(null, false)]
    public void IsAllowedPlayUrl_only_local_torrserver_play(string? url, bool expected)
    {
        var opts = Options.Create(new TorrServerOptions { Port = 8090 });
        var proxy = new TorrServerStreamProxy(
            httpClientFactory: null!,
            opts,
            NullLogger<TorrServerStreamProxy>.Instance);
        proxy.IsAllowedPlayUrl(url).Should().Be(expected);
    }
}
