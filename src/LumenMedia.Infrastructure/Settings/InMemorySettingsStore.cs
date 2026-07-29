using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Settings;

/// <summary>Thread-safe in-memory settings holder seeded from configuration on startup.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Lock _gate = new();
    private ServerSettingsDto _current;

    public InMemorySettingsStore(
        IOptions<PlaybackOptions> playback,
        IOptions<ImportOptions> import,
        IOptions<MetadataOptions> metadata)
    {
        var p = playback.Value;
        var i = import.Value;
        var m = metadata.Value;
        _current = new ServerSettingsDto
        {
            Transcoding = new TranscodingSettingsDto
            {
                HardwareAccel = p.HardwareAccel,
                MaxConcurrentSessions = p.MaxConcurrentSessions,
                AbrEnabled = p.AbrEnabled,
                SegmentDurationSec = p.SegmentDurationSec,
                DefaultRemoteCapKbps = p.DefaultRemoteCapKbps,
                HdrToneMapMethod = string.IsNullOrWhiteSpace(p.HdrToneMapMethod) ? "hable" : p.HdrToneMapMethod,
                Ladder = p.EffectiveLadder
                    .Select(r => new LadderRungDto { Id = r.Id, Height = r.Height, VideoBitrateKbps = r.VideoBitrateKbps })
                    .ToList(),
            },
            Metadata = new MetadataSettingsDto
            {
                Language = m.Language,
                FallbackLanguage = m.FallbackLanguage,
            },
            Import = new ImportSettingsDto
            {
                Watch = i.Watch,
                MinFileSizeMb = i.MinFileSizeMb,
                Strategy = i.Strategy,
            },
        };
    }

    public ServerSettingsDto Get()
    {
        lock (_gate)
            return _current;
    }

    public ServerSettingsDto Update(ServerSettingsDto patch)
    {
        lock (_gate)
        {
            _current = new ServerSettingsDto
            {
                Transcoding = patch.Transcoding,
                Metadata = new MetadataSettingsDto
                {
                    Language = patch.Metadata.Language,
                    FallbackLanguage = patch.Metadata.FallbackLanguage,
                },
                Import = patch.Import,
            };
            return _current;
        }
    }
}
