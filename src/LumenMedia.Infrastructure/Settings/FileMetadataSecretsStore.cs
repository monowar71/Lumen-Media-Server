using System.Text.Json;
using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Settings;

/// <summary>
/// Persists metadata API keys under <c>{Config}/metadata-secrets.json</c>.
/// Env/config values seed the store when the file is missing or a field is empty.
/// </summary>
public sealed class FileMetadataSecretsStore : IMetadataSecretsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly ILogger<FileMetadataSecretsStore> _logger;
    private string? _tmdbApiKey;
    private string? _tvdbApiKey;
    private string? _tvdbPin;

    public FileMetadataSecretsStore(
        IOptions<PathsOptions> paths,
        IOptions<MetadataOptions> metadata,
        ILogger<FileMetadataSecretsStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(paths.Value.Config);
        _path = Path.Combine(paths.Value.Config, "metadata-secrets.json");

        var m = metadata.Value;
        _tmdbApiKey = NullIfEmpty(m.Tmdb.ApiKey);
        _tvdbApiKey = NullIfEmpty(m.Tvdb.ApiKey);
        _tvdbPin = NullIfEmpty(m.Tvdb.Pin);

        LoadFromDisk();
    }

    public string? TmdbApiKey
    {
        get { lock (_gate) return _tmdbApiKey; }
    }

    public string? TvdbApiKey
    {
        get { lock (_gate) return _tvdbApiKey; }
    }

    public string? TvdbPin
    {
        get { lock (_gate) return _tvdbPin; }
    }

    public bool TmdbConfigured => !string.IsNullOrWhiteSpace(TmdbApiKey);
    public bool TvdbConfigured => !string.IsNullOrWhiteSpace(TvdbApiKey);

    public void Update(string? tmdbApiKey, string? tvdbApiKey, string? tvdbPin)
    {
        lock (_gate)
        {
            if (tmdbApiKey is not null)
                _tmdbApiKey = NullIfEmpty(tmdbApiKey);
            if (tvdbApiKey is not null)
                _tvdbApiKey = NullIfEmpty(tvdbApiKey);
            if (tvdbPin is not null)
                _tvdbPin = NullIfEmpty(tvdbPin);

            PersistUnlocked();
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            // Persist env-seeded values so they survive restarts without re-setting env.
            if (TmdbConfigured || TvdbConfigured)
                PersistUnlocked();
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<SecretsFile>(json, JsonOptions);
            if (file is null)
                return;

            // File wins over env when present (admin override).
            if (!string.IsNullOrWhiteSpace(file.TmdbApiKey))
                _tmdbApiKey = file.TmdbApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(file.TvdbApiKey))
                _tvdbApiKey = file.TvdbApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(file.TvdbPin))
                _tvdbPin = file.TvdbPin.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata secrets from {Path}", _path);
        }
    }

    private void PersistUnlocked()
    {
        try
        {
            var payload = new SecretsFile
            {
                TmdbApiKey = _tmdbApiKey,
                TvdbApiKey = _tvdbApiKey,
                TvdbPin = _tvdbPin,
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist metadata secrets to {Path}", _path);
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SecretsFile
    {
        public string? TmdbApiKey { get; set; }
        public string? TvdbApiKey { get; set; }
        public string? TvdbPin { get; set; }
    }
}
