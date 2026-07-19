namespace FreePlex.Application.Abstractions;

/// <summary>
/// Live store for metadata provider API keys. Seeded from env/config, overridable via admin settings,
/// persisted under the config directory. Values must never be logged or returned in GET responses.
/// </summary>
public interface IMetadataSecretsStore
{
    string? TmdbApiKey { get; }
    string? TvdbApiKey { get; }
    string? TvdbPin { get; }

    bool TmdbConfigured { get; }
    bool TvdbConfigured { get; }

    /// <summary>
    /// Updates keys. Empty string clears a key. Null means "leave unchanged".
    /// </summary>
    void Update(string? tmdbApiKey, string? tvdbApiKey, string? tvdbPin);
}
