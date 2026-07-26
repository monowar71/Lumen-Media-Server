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
}

public sealed record SetQualityRequest
{
    public required string QualityId { get; init; }
    public PlaybackMode Mode { get; init; } = PlaybackMode.Manual;
    public long ResumePositionMs { get; init; }
    public Guid? AudioStreamId { get; init; }
    public Guid? SubtitleStreamId { get; init; }
}

public sealed record SeekRequest
{
    public long PositionMs { get; init; }
}
