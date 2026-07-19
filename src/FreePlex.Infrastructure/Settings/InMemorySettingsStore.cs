using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Playback;
using FreePlex.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace FreePlex.Infrastructure.Settings;

/// <summary>Thread-safe in-memory settings holder seeded from configuration on startup.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Lock _gate = new();
    private ServerSettingsDto _current;

    public InMemorySettingsStore(IOptions<PlaybackOptions> playback, IOptions<ImportOptions> import)
    {
        var p = playback.Value;
        var i = import.Value;
        _current = new ServerSettingsDto
        {
            Transcoding = new TranscodingSettingsDto
            {
                HardwareAccel = p.HardwareAccel,
                MaxConcurrentSessions = p.MaxConcurrentSessions,
                AbrEnabled = p.AbrEnabled,
                SegmentDurationSec = p.SegmentDurationSec,
                DefaultRemoteCapKbps = p.DefaultRemoteCapKbps,
                Ladder = p.Ladder
                    .Select(r => new LadderRungDto { Id = r.Id, Height = r.Height, VideoBitrateKbps = r.VideoBitrateKbps })
                    .ToList(),
            },
            Metadata = new MetadataSettingsDto
            {
                Providers = ["Tmdb", "Tvdb", "Fanart"],
                Language = "en-US",
                FallbackLanguage = "en-US",
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
                Metadata = patch.Metadata,
                Import = patch.Import,
            };
            return _current;
        }
    }
}
