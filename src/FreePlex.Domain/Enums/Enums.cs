using System.Text.Json.Serialization;

namespace FreePlex.Domain.Enums;

public enum MediaKind
{
    Movie,
    Series,
    Episode
}

public enum LibraryType
{
    Movies,
    Series
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
