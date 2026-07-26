using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace LumenMedia.Api.IntegrationTests;

/// <summary>
/// End-to-end playback: decision → Range download / HLS segments → seek → stop.
/// Requires ffmpeg on PATH (skipped otherwise).
/// </summary>
public class PlaybackStreamTests(LumenMediaApiFactory factory) : IClassFixture<LumenMediaApiFactory>
{
    private readonly LumenMediaApiFactory _factory = factory;

    [Fact]
    public async Task DirectPlay_Range_and_Transcode_Hls_seek_stop_work()
    {
        if (!HasFfmpeg())
            return; // environment without ffmpeg — unit tests still cover argv

        var client = _factory.CreateClient();
        var mediaDir = Path.Combine(Path.GetTempPath(), $"lumenmedia-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaDir);
        var videoPath = Path.Combine(mediaDir, "Sample Movie (2020).mp4");
        var srtPath = Path.Combine(mediaDir, "Sample Movie (2020).en.srt");

        try
        {
            await CreateFixtureAsync(videoPath, srtPath);

            await client.PostAsJsonAsync("/api/v1/setup", new { username = "root", password = "password123" });
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "root", password = "password123" });
            login.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var createLib = await client.PostAsJsonAsync("/api/v1/libraries", new
            {
                name = "Movies",
                type = "Movies",
                paths = new[] { mediaDir },
            });
            createLib.StatusCode.Should().Be(HttpStatusCode.Created);
            var libraryId = JsonDocument.Parse(await createLib.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

            var scan = await client.PostAsync($"/api/v1/libraries/{libraryId}/scan", null);
            scan.StatusCode.Should().Be(HttpStatusCode.Accepted);

            // Wait for scanner to import the fixture.
            string? itemId = null;
            for (var i = 0; i < 40; i++)
            {
                var items = await client.GetFromJsonAsync<JsonElement>($"/api/v1/libraries/{libraryId}/items");
                if (items.GetProperty("total").GetInt32() > 0)
                {
                    itemId = items.GetProperty("items")[0].GetProperty("id").GetString();
                    break;
                }

                await Task.Delay(250);
            }

            itemId.Should().NotBeNullOrEmpty();

            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/items/{itemId}");
            detail.GetProperty("mediaSources").GetArrayLength().Should().BeGreaterThan(0);

            // DirectPlay with a browser-friendly profile.
            var dp = await client.PostAsJsonAsync("/api/v1/playback/decision", new
            {
                mediaId = itemId,
                mode = "auto",
                resumePositionMs = 0,
                profile = new
                {
                    maxResolution = "1080p",
                    maxBitrateKbps = 20000,
                    videoCodecs = new[] { "h264" },
                    audioCodecs = new[] { "aac" },
                    containers = new[] { "mp4", "mkv", "hls" },
                    subtitleFormats = new[] { "vtt", "srt" },
                    supportsHevc = false,
                    supportsHdr = false,
                },
            });
            dp.StatusCode.Should().Be(HttpStatusCode.Created);
            var dpBody = JsonDocument.Parse(await dp.Content.ReadAsStringAsync()).RootElement;
            dpBody.GetProperty("method").GetString().Should().Be("DirectPlay");
            var downloadUrl = dpBody.GetProperty("streamUrl").GetString()!;
            downloadUrl.Should().StartWith("/api/v1/stream/");
            downloadUrl.Should().EndWith("/source");

            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            rangeReq.Headers.Range = new RangeHeaderValue(0, 1023);
            var range = await client.SendAsync(rangeReq);
            range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await range.Content.ReadAsByteArrayAsync()).Length.Should().Be(1024);

            // Native players (Android ExoPlayer) keep the stream URL after the 15‑minute access
            // JWT expires — media under /stream/{sessionId} must work without Authorization.
            using var anonClient = _factory.CreateClient();
            using var anonRangeReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            anonRangeReq.Headers.Range = new RangeHeaderValue(0, 511);
            var anonRange = await anonClient.SendAsync(anonRangeReq);
            anonRange.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await anonRange.Content.ReadAsByteArrayAsync()).Length.Should().Be(512);

            // Expired access_token in the query string must not 401 stream URLs either.
            using var expiredReq = new HttpRequestMessage(
                HttpMethod.Get,
                downloadUrl + "?access_token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.invalid");
            expiredReq.Headers.Range = new RangeHeaderValue(0, 255);
            var expiredRange = await anonClient.SendAsync(expiredReq);
            expiredRange.StatusCode.Should().Be(HttpStatusCode.PartialContent);

            // Force Transcode by rejecting the source video codec.
            var tc = await client.PostAsJsonAsync("/api/v1/playback/decision", new
            {
                mediaId = itemId,
                mode = "manual",
                qualityId = "original",
                resumePositionMs = 0,
                profile = new
                {
                    maxResolution = "1080p",
                    maxBitrateKbps = 8000,
                    videoCodecs = new[] { "hevc" },
                    audioCodecs = new[] { "aac" },
                    containers = new[] { "mp4", "hls" },
                    subtitleFormats = new[] { "vtt", "srt" },
                    supportsHevc = true,
                    supportsHdr = false,
                },
            });
            if (tc.StatusCode != HttpStatusCode.Created)
            {
                var err = await tc.Content.ReadAsStringAsync();
                throw new Xunit.Sdk.XunitException($"Transcode decision failed: {(int)tc.StatusCode} {err}");
            }
            var tcBody = JsonDocument.Parse(await tc.Content.ReadAsStringAsync()).RootElement;
            var sessionId = tcBody.GetProperty("sessionId").GetString()!;
            tcBody.GetProperty("method").GetString().Should().BeOneOf("Transcode", "DirectStream");

            string? playlist = null;
            for (var i = 0; i < 60; i++)
            {
                var pl = await client.GetAsync($"/api/v1/stream/{sessionId}/index.m3u8");
                if (pl.StatusCode == HttpStatusCode.OK)
                {
                    playlist = await pl.Content.ReadAsStringAsync();
                    if (playlist.Contains(".m4s", StringComparison.OrdinalIgnoreCase))
                        break;
                }

                await Task.Delay(250);
            }

            playlist.Should().NotBeNullOrEmpty();
            playlist.Should().Contain("init.mp4");

            var init = await client.GetAsync($"/api/v1/stream/{sessionId}/init.mp4");
            init.StatusCode.Should().Be(HttpStatusCode.OK);
            (await init.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);

            // HLS playlist/segment GETs must succeed without a bearer token (session capability).
            using var anonHls = _factory.CreateClient();
            var anonPlaylist = await anonHls.GetAsync($"/api/v1/stream/{sessionId}/index.m3u8");
            anonPlaylist.StatusCode.Should().Be(HttpStatusCode.OK);

            var seek = await client.PostAsJsonAsync($"/api/v1/playback/{sessionId}/seek", new { positionMs = 2000 });
            seek.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await seek.Content.ReadAsStringAsync()).RootElement
                .GetProperty("startPositionMs").GetInt64().Should().Be(2000);

            for (var i = 0; i < 40; i++)
            {
                var pl = await client.GetAsync($"/api/v1/stream/{sessionId}/index.m3u8");
                if (pl.StatusCode == HttpStatusCode.OK)
                    break;
                await Task.Delay(250);
            }

            var stop = await client.PostAsync($"/api/v1/playback/{sessionId}/stop", null);
            stop.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.GetAsync($"/api/v1/stream/{sessionId}/index.m3u8")).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Progress + continue-watching for a movie.
            var progress = await client.PutAsJsonAsync($"/api/v1/progress/{itemId}", new
            {
                positionMs = 1500,
                durationMs = 5000,
                state = "paused",
            });
            progress.StatusCode.Should().Be(HttpStatusCode.OK);
            var cw = await client.GetFromJsonAsync<JsonElement>("/api/v1/continue-watching");
            cw.GetProperty("total").GetInt32().Should().BeGreaterThan(0);

            // External SRT → WebVTT when present on the source.
            var streams = detail.GetProperty("mediaSources")[0].GetProperty("streams");
            string? subId = null;
            foreach (var s in streams.EnumerateArray())
            {
                if (s.GetProperty("kind").GetString() == "Subtitle")
                {
                    subId = s.GetProperty("id").GetString();
                    break;
                }
            }

            if (subId is not null)
            {
                var vtt = await client.GetAsync($"/api/v1/items/{itemId}/subtitles/{subId}.vtt");
                vtt.StatusCode.Should().Be(HttpStatusCode.OK);
                var body = await vtt.Content.ReadAsStringAsync();
                body.Should().Contain("WEBVTT");
            }
        }
        finally
        {
            try { Directory.Delete(mediaDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static bool HasFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(5));
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static async Task CreateFixtureAsync(string videoPath, string srtPath)
    {
        // ~5s silent h264/aac mp4.
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-y",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=24",
                     "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                     "-t", "5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
                     "-shortest", videoPath,
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0);

        await File.WriteAllTextAsync(srtPath,
            """
            1
            00:00:00,000 --> 00:00:02,000
            Fixture subtitle
            """);
    }
}
