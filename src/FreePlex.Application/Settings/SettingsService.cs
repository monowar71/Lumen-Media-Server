using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Metadata;

namespace FreePlex.Application.Settings;

public sealed class SettingsService(ISettingsStore store, MetadataJobService metadataJobs)
{
    public ServerSettingsDto Get() => store.Get();

    public async Task<ServerSettingsDto> UpdateAsync(ServerSettingsDto patch, CancellationToken ct)
    {
        var previous = store.Get();
        var updated = store.Update(patch);

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

        return updated;
    }
}
