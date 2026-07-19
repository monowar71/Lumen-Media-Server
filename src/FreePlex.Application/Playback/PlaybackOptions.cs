namespace FreePlex.Application.Playback;

public sealed class LadderRung
{
    public string Id { get; set; } = null!;
    public int Height { get; set; }
    public int VideoBitrateKbps { get; set; }
}

/// <summary>Bound from configuration section <c>FreePlex:Transcoding</c>.</summary>
public sealed class PlaybackOptions
{
    public const string SectionName = "FreePlex:Transcoding";

    public string HardwareAccel { get; set; } = "auto";
    /// <summary>VAAPI render node (Linux). Used when <see cref="HardwareAccel"/> is vaapi.</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public int MaxConcurrentSessions { get; set; } = 3;
    /// <summary>Steady-state HLS segment length. First segment uses <see cref="InitialSegmentDurationSec"/>.</summary>
    public int SegmentDurationSec { get; set; } = 2;
    /// <summary>Target length of the first HLS segment for faster time-to-first-frame.</summary>
    public int InitialSegmentDurationSec { get; set; } = 1;
    public bool AbrEnabled { get; set; } = true;
    public int DefaultRemoteCapKbps { get; set; } = 8000;

    /// <summary>When true, pause ffmpeg once it is more than <see cref="MaxAheadSegments"/> ahead of the player.</summary>
    public bool Throttle { get; set; } = true;

    /// <summary>Max HLS media segments written ahead of the last segment the player requested.</summary>
    public int MaxAheadSegments { get; set; } = 15;

    /// <summary>Stop sessions with no playlist/segment/ping traffic for this long.</summary>
    public int IdleTimeoutSec { get; set; } = 120;

    public List<LadderRung> Ladder { get; set; } =
    [
        new() { Id = "1080p", Height = 1080, VideoBitrateKbps = 10000 },
        new() { Id = "720p", Height = 720, VideoBitrateKbps = 4000 },
        new() { Id = "480p", Height = 480, VideoBitrateKbps = 1500 },
        new() { Id = "360p", Height = 360, VideoBitrateKbps = 700 },
    ];
}
