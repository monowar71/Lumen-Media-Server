using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Torrents;

public sealed class TorrServerClient(
    IHttpClientFactory httpClientFactory,
    ITorrServerProcess process,
    IOptions<TorrServerOptions> options,
    ILogger<TorrServerClient> logger) : ITorrServerClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private TorrServerOptions Opts => options.Value;

    public async Task<string> EchoAsync(CancellationToken ct)
    {
        await process.EnsureRunningAsync(ct);
        var client = CreateClient();
        return (await client.GetStringAsync("echo", ct)).Trim();
    }

    public async Task<TorrServerTorrentStatus> UploadTorrentAsync(string torrentFilePath, CancellationToken ct)
    {
        await process.EnsureRunningAsync(ct);
        var client = CreateClient();

        await using var fs = File.OpenRead(torrentFilePath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        content.Add(fileContent, "file", Path.GetFileName(torrentFilePath));
        content.Add(new StringContent("true"), "save");

        using var resp = await client.PostAsync("torrent/upload", content, ct);
        resp.EnsureSuccessStatusCode();
        var status = await resp.Content.ReadFromJsonAsync<TorrServerStatusDto>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Empty TorrServer upload response.");
        return Map(status);
    }

    public async Task<TorrServerTorrentStatus?> GetAsync(string infoHash, CancellationToken ct)
    {
        await process.EnsureRunningAsync(ct);
        var client = CreateClient();
        using var resp = await client.PostAsJsonAsync(
            "torrents",
            new { action = "get", hash = infoHash },
            JsonOpts,
            ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        var status = await resp.Content.ReadFromJsonAsync<TorrServerStatusDto>(JsonOpts, ct);
        return status is null ? null : Map(status);
    }

    public async Task DropAsync(string infoHash, CancellationToken ct)
    {
        if (!process.IsRunning && Opts.ManageProcess)
            return;

        try
        {
            await process.EnsureRunningAsync(ct);
            var client = CreateClient();
            using var resp = await client.PostAsJsonAsync(
                "torrents",
                new { action = "drop", hash = infoHash },
                JsonOpts,
                ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogDebug("TorrServer drop {Hash} returned {Status}", infoHash, resp.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TorrServer drop failed for {Hash}", infoHash);
        }
    }

    public string BuildPlayUrl(string infoHash, int fileIndex) =>
        $"{Opts.ResolveBaseUrl()}/play/{infoHash.ToLowerInvariant()}/{fileIndex}";

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("TorrServer");
        client.BaseAddress = new Uri(Opts.ResolveBaseUrl().TrimEnd('/') + "/");
        return client;
    }

    private static TorrServerTorrentStatus Map(TorrServerStatusDto dto) =>
        new(
            dto.Hash ?? string.Empty,
            dto.Name ?? dto.Title,
            dto.Stat,
            dto.StatString,
            (dto.FileStats ?? [])
                .Select(f => new TorrServerFileStat(f.Id, f.Path ?? string.Empty, f.Length))
                .ToList(),
            dto.ConnectedSeeders,
            dto.TotalPeers,
            dto.ActivePeers,
            dto.DownloadSpeed);

    private sealed class TorrServerStatusDto
    {
        public string? Title { get; set; }
        public string? Hash { get; set; }
        public string? Name { get; set; }
        public int Stat { get; set; }
        [JsonPropertyName("stat_string")]
        public string? StatString { get; set; }
        [JsonPropertyName("file_stats")]
        public List<TorrServerFileStatDto>? FileStats { get; set; }
        [JsonPropertyName("connected_seeders")]
        public int ConnectedSeeders { get; set; }
        [JsonPropertyName("total_peers")]
        public int TotalPeers { get; set; }
        [JsonPropertyName("active_peers")]
        public int ActivePeers { get; set; }
        [JsonPropertyName("download_speed")]
        public double DownloadSpeed { get; set; }
    }

    private sealed class TorrServerFileStatDto
    {
        public int Id { get; set; }
        public string? Path { get; set; }
        public long Length { get; set; }
    }
}
