using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Metadata;

namespace FreePlex.Application.Settings;

public sealed class SettingsService(
    ISettingsStore store,
    IMetadataSecretsStore secrets,
    MetadataJobService metadataJobs)
{
    public ServerSettingsDto Get() => Project(store.Get());

    public async Task<ServerSettingsDto> UpdateAsync(ServerSettingsDto patch, CancellationToken ct)
    {
        var previous = store.Get();

        // Apply write-only API keys before projecting (empty string clears).
        if (patch.Metadata.TmdbApiKey is not null
            || patch.Metadata.TvdbApiKey is not null
            || patch.Metadata.TvdbPin is not null)
        {
            secrets.Update(
                patch.Metadata.TmdbApiKey,
                patch.Metadata.TvdbApiKey,
                patch.Metadata.TvdbPin);
        }

        var sanitized = patch with
        {
            Metadata = patch.Metadata with
            {
                TmdbApiKey = null,
                TvdbApiKey = null,
                TvdbPin = null,
            },
        };

        var updated = store.Update(sanitized);

        var languageChanged = !string.Equals(
                                  previous.Metadata.Language,
                                  updated.Metadata.Language,
                                  StringComparison.OrdinalIgnoreCase)
                              || !string.Equals(
                                  previous.Metadata.FallbackLanguage,
                                  updated.Metadata.FallbackLanguage,
                                  StringComparison.OrdinalIgnoreCase);

        if (languageChanged)
            await metadataJobs.EnqueueRefreshAllAsync(ct);

        return Project(updated);
    }

    private ServerSettingsDto Project(ServerSettingsDto raw)
    {
        var providers = new List<string>();
        if (secrets.TmdbConfigured)
            providers.Add("Tmdb");
        providers.Add("TvMaze");
        if (secrets.TvdbConfigured)
            providers.Add("Tvdb");

        return raw with
        {
            Metadata = raw.Metadata with
            {
                Providers = providers,
                TmdbConfigured = secrets.TmdbConfigured,
                TvdbConfigured = secrets.TvdbConfigured,
                TvMazeConfigured = true,
                TmdbApiKey = null,
                TvdbApiKey = null,
                TvdbPin = null,
            },
        };
    }
}
