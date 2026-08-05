using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;
using LumenMedia.Domain.Users;

namespace LumenMedia.Application.Common;

public static class UserMapper
{
    public static UserDto Map(User user)
    {
        object access = user.Role == UserRole.Admin || user.LibraryAccessAll
            ? "*"
            : user.AllowedLibraryIds.ToArray();

        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Role = user.Role,
            LibraryAccess = access,
            AllowTranscoding = user.AllowTranscoding,
            MaxBitrateKbpsRemote = user.MaxBitrateRemoteKbps,
            CreatedAt = user.CreatedAt,
        };
    }
}

public static class MediaMapper
{
    private static ArtworkUrls ItemArtwork(MediaItem item)
    {
        var kinds = item.Artworks.Select(a => a.Kind).ToHashSet();
        return new ArtworkUrls
        {
            Poster = kinds.Contains(ArtworkKind.Poster) ? ArtworkUrlBuilder.ItemArtwork(item.Id, ArtworkKind.Poster) : null,
            Backdrop = kinds.Contains(ArtworkKind.Backdrop) ? ArtworkUrlBuilder.ItemArtwork(item.Id, ArtworkKind.Backdrop) : null,
            Logo = kinds.Contains(ArtworkKind.Logo) ? ArtworkUrlBuilder.ItemArtwork(item.Id, ArtworkKind.Logo) : null,
            Banner = kinds.Contains(ArtworkKind.Banner) ? ArtworkUrlBuilder.ItemArtwork(item.Id, ArtworkKind.Banner) : null,
        };
    }

    private static List<PersonDto> MapPeople(MediaItem item) =>
        item.People.OrderBy(p => p.SortOrder)
            .Select(p => new PersonDto
            {
                Name = p.Person.Name,
                Role = p.Role,
                Type = p.Type.ToString(),
                Order = p.SortOrder,
                Thumb = p.Person.ThumbPath,
            }).ToList();

    public static UserDataDto MapUserData(PlaybackProgress? p) => new()
    {
        Watched = p?.Watched ?? false,
        PlaybackPositionMs = p?.PositionMs ?? 0,
        IsFavorite = p?.IsFavorite ?? false,
    };

    public static MediaStreamDto MapStream(MediaStream s) => new()
    {
        Id = s.Id,
        Kind = s.Kind,
        Index = s.StreamIndex,
        Codec = SanitizeCodec(s.Codec),
        Profile = s.Profile,
        Language = s.Language,
        Title = s.Title,
        IsDefault = s.IsDefault,
        IsForced = s.IsForced,
        Width = s.Width,
        Height = s.Height,
        FrameRate = s.FrameRate,
        BitrateKbps = s.BitrateKbps,
        Hdr = s.Hdr,
        Channels = s.Channels,
        SampleRate = s.SampleRate,
        IsExternal = s.IsExternal,
        Format = s.SubtitleFormat,
    };

    /// <summary>Compact HUD snapshot from probed / stored streams.</summary>
    public static ProbedFormatDto? MapProbedFormat(IReadOnlyList<MediaStream> streams)
    {
        var video = streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        var audio = streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.IsDefault)
                    ?? streams.FirstOrDefault(s => s.Kind == StreamKind.Audio);
        if (video is null && audio is null)
            return null;

        return new ProbedFormatDto
        {
            VideoCodec = SanitizeCodec(video?.Codec),
            VideoHdr = video?.Hdr,
            Width = video?.Width,
            Height = video?.Height,
            AudioCodec = SanitizeCodec(audio?.Codec),
            AudioChannels = audio?.Channels,
            AudioTitle = audio?.Title,
        };
    }

    /// <summary>Drop probe placeholders so clients do not show "UNKNOWN → H.264".</summary>
    public static string? SanitizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;
        return codec.Equals("unknown", StringComparison.OrdinalIgnoreCase)
               || codec.Equals("und", StringComparison.OrdinalIgnoreCase)
               || codec.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : codec;
    }

    public static MediaSourceDto MapSource(MediaSource src, bool includePath) => new()
    {
        Id = src.Id,
        Path = includePath ? src.Path : null,
        Container = src.Container,
        SizeBytes = src.SizeBytes,
        DurationMs = src.DurationMs,
        OverallBitrateKbps = src.OverallBitrateKbps,
        Streams = src.Streams.OrderBy(s => s.StreamIndex).Select(MapStream).ToList(),
    };

    public static MovieDetail MapMovieDetail(
        Movie movie,
        PlaybackProgress? progress,
        bool includePath,
        bool hasTheme = false) => new()
    {
        Id = movie.Id,
        Title = movie.Title,
        OriginalTitle = movie.OriginalTitle,
        SortTitle = movie.SortTitle,
        Year = movie.Year,
        ReleaseDate = movie.ReleaseDate,
        Overview = movie.Overview,
        Tagline = movie.Tagline,
        RuntimeMs = movie.RuntimeMs,
        CommunityRating = movie.CommunityRating,
        OfficialRating = movie.OfficialRating,
        Genres = movie.Genres.Select(g => g.Name).ToList(),
        People = MapPeople(movie),
        Studios = movie.Studios.ToList(),
        TrailerUrl = movie.TrailerUrl,
        ThemeUrl = hasTheme ? ArtworkUrlBuilder.ItemTheme(movie.Id) : null,
        ExternalIds = new ExternalIds { Tmdb = movie.TmdbId, Tvdb = movie.TvdbId, Imdb = movie.ImdbId },
        MetadataLocked = movie.MetadataLocked,
        Artwork = ItemArtwork(movie),
        MediaSources = movie.Sources.Select(s => MapSource(s, includePath)).ToList(),
        UserData = MapUserData(progress),
        LibraryId = movie.LibraryId,
        AddedAt = movie.AddedAt,
        UpdatedAt = movie.UpdatedAt,
    };

    public static SeriesDetail MapSeriesDetail(
        Series series,
        int seasonCount,
        int episodeCount,
        int unwatched,
        EpisodeSummary? nextUp = null,
        bool hasTheme = false) => new()
    {
        Id = series.Id,
        Title = series.Title,
        OriginalTitle = series.OriginalTitle,
        Year = series.Year,
        EndYear = series.EndYear,
        Status = series.Status?.ToString(),
        Overview = series.Overview,
        CommunityRating = series.CommunityRating,
        OfficialRating = series.OfficialRating,
        Genres = series.Genres.Select(g => g.Name).ToList(),
        People = MapPeople(series),
        Studios = series.Studios.ToList(),
        TrailerUrl = series.TrailerUrl,
        ThemeUrl = hasTheme ? ArtworkUrlBuilder.ItemTheme(series.Id) : null,
        ExternalIds = new ExternalIds { Tmdb = series.TmdbId, Tvdb = series.TvdbId, Imdb = series.ImdbId },
        MetadataLocked = series.MetadataLocked,
        SeasonCount = seasonCount,
        EpisodeCount = episodeCount,
        Artwork = ItemArtwork(series),
        UserData = new SeriesUserData { UnwatchedEpisodeCount = unwatched, NextUp = nextUp },
        LibraryId = series.LibraryId,
        AddedAt = series.AddedAt,
    };

    public static SeasonDto MapSeason(Season season, int episodeCount) => new()
    {
        Id = season.Id,
        SeriesId = season.SeriesId,
        SeasonNumber = season.SeasonNumber,
        Name = season.Name,
        EpisodeCount = episodeCount,
    };

    public static EpisodeSummary MapEpisodeSummary(Episode e, PlaybackProgress? progress) => new()
    {
        Id = e.Id,
        SeriesId = e.SeriesId,
        SeasonId = e.SeasonId,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.EpisodeNumber,
        Title = e.Title,
        Overview = e.Overview,
        AirDate = e.AirDate,
        RuntimeMs = e.RuntimeMs,
        Artwork = new ArtworkUrls { Thumb = ArtworkUrlBuilder.ItemArtwork(e.Id, ArtworkKind.Thumb) },
        UserData = MapUserData(progress),
    };

    public static EpisodeDetail MapEpisodeDetail(Episode e, PlaybackProgress? progress, bool includePath) => new()
    {
        Id = e.Id,
        SeriesId = e.SeriesId,
        SeasonId = e.SeasonId,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.EpisodeNumber,
        Title = e.Title,
        Overview = e.Overview,
        AirDate = e.AirDate,
        RuntimeMs = e.RuntimeMs,
        Artwork = new ArtworkUrls { Thumb = ArtworkUrlBuilder.ItemArtwork(e.Id, ArtworkKind.Thumb) },
        MediaSources = e.Sources.Select(s => MapSource(s, includePath)).ToList(),
        UserData = MapUserData(progress),
    };
}
