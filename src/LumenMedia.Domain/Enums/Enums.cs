using System.Text.Json.Serialization;

namespace LumenMedia.Domain.Enums;

public enum MediaKind
{
    Movie,
    Series,
    Episode
}

public enum LibraryType
{
    Movies,
    Series,
    /// <summary>Catalog of <c>.torrent</c> files; playback streams via embedded TorrServer.</summary>
    Torrent
}

/// <summary>How <see cref="Media.MediaSource"/> bytes are obtained at playback time.</summary>
public enum MediaSourceKind
{
    LocalFile,
    Torrent
}

public enum StreamKind
{
    Video,
    Audio,
    Subtitle
}

public enum PlaybackMethod
{
    DirectPlay,
    DirectStream,
    Transcode
}

public enum PlaybackMode
{
    // api.md uses lowercase "auto" | "manual" on the wire.
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("manual")]
    Manual
}

public enum ArtworkKind
{
    Poster,
    Backdrop,
    Logo,
    Thumb,
    Banner
}

public enum JobType
{
    ScanLibrary,
    ImportFile,
    FetchMetadata,
    GenerateArtwork,
    CleanupTranscodes
}

/// <summary>Scope for admin-triggered library-wide metadata enrichment.</summary>
public enum MetadataRefreshMode
{
    /// <summary>Items without overview or TMDB id (same set as post-scan enqueue).</summary>
    Missing,

    /// <summary>Items that already have TMDB/TVDB ids (re-fetch / language refresh).</summary>
    Matched,

    /// <summary>Every movie/series in the library.</summary>
    All
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum UserRole
{
    Admin,
    User
}

public enum ImportStatus
{
    Pending,
    Matched,
    Unmatched,
    Imported,
    Failed
}

public enum SeriesStatus
{
    Continuing,
    Ended
}

public enum PersonType
{
    Actor,
    Director,
    Writer
}
