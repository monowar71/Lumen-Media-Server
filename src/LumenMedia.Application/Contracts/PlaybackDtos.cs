using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Contracts;

public sealed record DeviceProfile
{
    public string? MaxResolution { get; init; }
    public int? MaxBitrateKbps { get; init; }
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];
    public IReadOnlyList<string> AudioCodecs { get; init; } = [];
    public IReadOnlyList<string> Containers { get; init; } = [];
    public IReadOnlyList<string> SubtitleFormats { get; init; } = [];
    public bool SupportsHevc { get; init; }
    public bool SupportsHdr { get; init; }
}

public sealed record PlaybackDecisionRequest
{
    public required Guid MediaId { get; init; }
    public Guid? MediaSourceId { get; init; }
    public PlaybackMode Mode { get; init; } = PlaybackMode.Auto;
    public string? QualityId { get; init; }
    public Guid? AudioStreamId { get; init; }
    public Guid? SubtitleStreamId { get; init; }
    public long ResumePositionMs { get; init; }
    public DeviceProfile Profile { get; init; } = new();
    /// <summary>Force HDR→SDR tonemap even when the device profile advertises HDR support.</summary>
    public bool ForceHdrToSdr { get; init; }
    /// <summary>Target channel layout id (<c>stereo</c>, <c>2.1</c>, <c>5.1</c>, <c>mono</c>). Null = server default.</summary>
    public string? AudioLayout { get; init; }
}

public sealed record AudioLayoutOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Channels { get; init; }
}

public sealed record QualityOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool Adaptive { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? BitrateKbps { get; init; }
}

public sealed record AudioStreamOption
{
    public required Guid Id { get; init; }
    public string? Language { get; init; }
    /// <summary>Container track title — often the dubbing studio (LostFilm, MovieDalen, …).</summary>
    public string? Title { get; init; }
    public string? Codec { get; init; }
    public int? Channels { get; init; }
    public bool IsDefault { get; init; }
}

public sealed record SubtitleStreamOption
{
    public required Guid Id { get; init; }
    public string? Language { get; init; }
    /// <summary>Container track title (e.g. "Russian (Forced)", "English (SDH)").</summary>
    public string? Title { get; init; }
    public string? Format { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public required string DeliveryUrl { get; init; }
}

public sealed record PlaybackDecisionResponse
{
    public required string SessionId { get; init; }
    public required PlaybackMethod Method { get; init; }
    public required PlaybackMode Mode { get; init; }
    public required string StreamUrl { get; init; }
    public required string Container { get; init; }
    public long StartPositionMs { get; init; }

    /// <summary>Probed media duration. Clients should prefer this over HLS
    /// <c>video.duration</c>, which only reflects segments written so far.</summary>
    public long? DurationMs { get; init; }

    public required string SelectedQualityId { get; init; }
    public IReadOnlyList<QualityOption> AvailableQualities { get; init; } = [];
    public IReadOnlyList<AudioStreamOption> AudioStreams { get; init; } = [];
    public IReadOnlyList<SubtitleStreamOption> SubtitleStreams { get; init; } = [];
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Debug reason (also mirrored via the X-Playback-Reason header).</summary>
    public string? Reason { get; init; }

    /// <summary>Source video HDR label from probe (<c>HDR10</c>, <c>HLG</c>, …), if any.</summary>
    public string? SourceHdr { get; init; }

    /// <summary>True when the active session applies HDR→SDR tonemap.</summary>
    public bool ToneMapActive { get; init; }

    /// <summary>Channel layouts available for the selected audio track (no upmix).</summary>
    public IReadOnlyList<AudioLayoutOption> AvailableAudioLayouts { get; init; } = [];

    /// <summary>Effective audio layout id for this session.</summary>
    public required string SelectedAudioLayout { get; init; }
}

public sealed record SetQualityRequest
{
    public required string QualityId { get; init; }
    public PlaybackMode Mode { get; init; } = PlaybackMode.Manual;
    public long ResumePositionMs { get; init; }
    public Guid? AudioStreamId { get; init; }
    public Guid? SubtitleStreamId { get; init; }
    public bool? ForceHdrToSdr { get; init; }
    public string? AudioLayout { get; init; }
}

public sealed record SeekRequest
{
    public long PositionMs { get; init; }
}
