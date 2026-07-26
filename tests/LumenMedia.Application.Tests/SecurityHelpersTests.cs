using System.Net;
using FluentAssertions;
using LumenMedia.Application.Common;

namespace LumenMedia.Application.Tests;

public sealed class SecurityHelpersTests
{
    [Fact]
    public void LikePattern_escapes_wildcards()
    {
        LikePattern.Contains("100%_off").Should().Be("%100\\%\\_off%");
    }

    [Fact]
    public void RemoteUrlSafety_allows_tmdb_artwork_host()
    {
        var url = RemoteUrlSafety.EnsureAllowedHttpsHost(
            "https://image.tmdb.org/t/p/w500/abc.jpg",
            RemoteUrlSafety.ArtworkHosts);
        url.Should().Contain("image.tmdb.org");
    }

    [Fact]
    public void RemoteUrlSafety_rejects_arbitrary_artwork_host()
    {
        var act = () => RemoteUrlSafety.EnsureAllowedHttpsHost(
            "https://evil.example/x.jpg",
            RemoteUrlSafety.ArtworkHosts);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void RemoteUrlSafety_blocks_cloud_metadata_ip()
    {
        var act = () => RemoteUrlSafety.EnsureSafeIntegrationUrl("http://169.254.169.254/latest/meta-data/");
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void RemoteUrlSafety_allows_lan_plex_url()
    {
        var uri = RemoteUrlSafety.EnsureSafeIntegrationUrl("http://192.168.0.10:32400");
        uri.Host.Should().Be("192.168.0.10");
    }

    [Fact]
    public void RemoteUrlSafety_blocks_loopback()
    {
        RemoteUrlSafety.IsBlockedDestination(IPAddress.Loopback).Should().BeTrue();
        RemoteUrlSafety.IsBlockedDestination(IPAddress.Parse("127.0.0.1")).Should().BeTrue();
    }

    [Fact]
    public void PathSafety_rejects_path_outside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumen-path-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), "lumen-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outside, "x");
            try
            {
                PathSafety.TryResolveUnderRoots(outside, [root], out _).Should().BeFalse();
            }
            finally
            {
                File.Delete(outside);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
