using FreePlex.Application.Abstractions;

namespace FreePlex.Infrastructure.Settings;

/// <summary>Reads the live metadata language from <see cref="ISettingsStore"/>.</summary>
public sealed class SettingsMetadataLanguageSource(ISettingsStore store) : IMetadataLanguageSource
{
    public MetadataLanguage Get()
    {
        var meta = store.Get().Metadata;
        return new MetadataLanguage(meta.Language, meta.FallbackLanguage);
    }
}
