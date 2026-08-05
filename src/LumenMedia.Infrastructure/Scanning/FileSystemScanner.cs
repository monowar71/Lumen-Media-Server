using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Scanning;

/// <summary>
/// Walks a library's paths, creating <see cref="MediaItem"/> + <see cref="MediaSource"/> rows.
/// ffprobe is optional: absent binary degrades to minimal stream info without crashing.
/// Existing source paths are skipped (idempotent re-scan).
/// Only files whose parsed kind matches the library type are imported, so Movies and Series
/// libraries may share one filesystem root without moving or linking files.
/// </summary>
public sealed class FileSystemScanner(
    IUnitOfWork uow,
    INameParser nameParser,
    ITorrentMetadataParser torrentParser,
    FfprobeClient ffprobe,
    TimeProvider clock,
    ExternalHistoryPromoter externalHistoryPromoter,
    ILogger<FileSystemScanner> logger) : IMediaScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".wmv", ".flv", ".m4v",
    };

    private static readonly HashSet<string> SampleNameMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "trailer", "preview", "rarbg.com", "rarbg",
    };

    public async Task<ScanResult> ScanAsync(Guid libraryId, IProgress<double>? progress, CancellationToken ct)
    {
        var library = await uow.Libraries.GetByIdAsync(libraryId, ct);
        if (library is null)
            return new ScanResult(0, 0, 0);

        if (library.Type == LibraryType.Torrent)
            return await ScanTorrentsAsync(library, progress, ct);

        return await ScanVideoFilesAsync(library, progress, ct);
    }

    private async Task<ScanResult> ScanVideoFilesAsync(
        Library library,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var libraryId = library.Id;
        var files = EnumerateVideoFiles(library.Paths.Select(p => p.Path)).ToList();
        var seriesCache = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skippedWrongKind = 0;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];

            var existing = await uow.Media.FindSourceByPathAsync(file, ct);
            if (existing is not null)
            {
                await RefreshStreamTagsIfNeededAsync(file, ct);
                progress?.Report((i + 1) / (double)files.Count);
                continue;
            }

            var parsed = nameParser.Parse(Path.GetFileName(file));
            if (!library.Type.Accepts(parsed.Kind))
            {
                skippedWrongKind++;
                logger.LogDebug(
                    "Skipping {File}: parsed as {Kind}, library is {LibraryType}",
                    file,
                    parsed.Kind,
                    library.Type);
                progress?.Report((i + 1) / (double)files.Count);
                continue;
            }

            try
            {
                var roots = library.Paths.Select(p => p.Path).ToList();
                if (!PathSafety.TryResolveUnderRoots(file, roots, out var safePath))
                {
                    logger.LogWarning("Skipping {File}: real path escapes library roots (symlink?)", file);
                    progress?.Report((i + 1) / (double)files.Count);
                    continue;
                }

                var imported = library.Type == LibraryType.Movies
                    ? await ImportMovieAsync(library.Id, safePath, parsed, ct)
                    : await ImportEpisodeAsync(library.Id, safePath, parsed, seriesCache, ct);
                if (imported)
                    added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                uow.DiscardChanges();
                seriesCache.Clear();
                logger.LogWarning(ex, "Failed to import {File}", file);
            }

            progress?.Report((i + 1) / (double)files.Count);
        }

        library = await uow.Libraries.GetByIdAsync(libraryId, ct) ?? library;
        library.MarkScanned(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        if (skippedWrongKind > 0)
            logger.LogInformation(
                "Library {LibraryId} scan skipped {Skipped} file(s) that belong to another media kind",
                libraryId,
                skippedWrongKind);
        return new ScanResult(added, 0, 0);
    }

    private async Task<ScanResult> ScanTorrentsAsync(
        Library library,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var libraryId = library.Id;
        var torrents = EnumerateTorrentFiles(library.Paths.Select(p => p.Path)).ToList();
        var seriesCache = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skippedWrongKind = 0;

        for (var i = 0; i < torrents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var torrentFile = torrents[i];
            var roots = library.Paths.Select(p => p.Path).ToList();
            if (!PathSafety.TryResolveUnderRoots(torrentFile, roots, out var safeTorrent))
            {
                logger.LogWarning("Skipping {File}: real path escapes library roots", torrentFile);
                progress?.Report(torrents.Count == 0 ? 1 : (i + 1) / (double)torrents.Count);
                continue;
            }

            TorrentMetadata meta;
            try
            {
                meta = torrentParser.ParseFile(safeTorrent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to parse torrent {File}", safeTorrent);
                progress?.Report((i + 1) / (double)torrents.Count);
                continue;
            }

            var videoEntries = meta.Files
                .Where(IsVideoTorrentEntry)
                .Where(f => !IsSampleOrTrailer(f.Path))
                .ToList();

            foreach (var entry in videoEntries)
            {
                var sourcePath = $"{safeTorrent}#{entry.Index}";
                var existing = await uow.Media.FindSourceByPathAsync(sourcePath, ct);
                if (existing is not null)
                    continue;

                var parseName = Path.GetFileName(entry.Path);
                if (string.IsNullOrWhiteSpace(parseName))
                    parseName = Path.GetFileNameWithoutExtension(safeTorrent);
                var parsed = nameParser.Parse(parseName);
                if (!library.Type.Accepts(parsed.Kind))
                {
                    skippedWrongKind++;
                    continue;
                }

                try
                {
                    var container = Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();
                    if (string.IsNullOrEmpty(container))
                        container = "mkv";
                    var mtime = File.Exists(safeTorrent)
                        ? new DateTimeOffset(File.GetLastWriteTimeUtc(safeTorrent), TimeSpan.Zero)
                        : clock.GetUtcNow();

                    var source = MediaSource.CreateTorrent(
                        safeTorrent,
                        meta.InfoHash,
                        entry.Index,
                        entry.Path,
                        container,
                        entry.Length,
                        mtime,
                        clock.GetUtcNow());
                    // No ffprobe at scan — empty codec until play-time probe; clients hide "unknown".
                    source.AddStream(new MediaStream(StreamKind.Video, 0));

                    var imported = parsed.Kind == MediaKind.Movie
                        ? await ImportTorrentMovieAsync(library.Id, source, parsed, ct)
                        : await ImportTorrentEpisodeAsync(library.Id, source, parsed, seriesCache, ct);
                    if (imported)
                        added++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    uow.DiscardChanges();
                    seriesCache.Clear();
                    logger.LogWarning(ex, "Failed to import torrent entry {File}#{Index}", safeTorrent, entry.Index);
                }
            }

            progress?.Report((i + 1) / (double)torrents.Count);
        }

        library = await uow.Libraries.GetByIdAsync(libraryId, ct) ?? library;
        library.MarkScanned(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        if (skippedWrongKind > 0)
            logger.LogInformation(
                "Torrent library {LibraryId} skipped {Skipped} non-matching entries",
                libraryId,
                skippedWrongKind);
        return new ScanResult(added, 0, 0);
    }

    private async Task<bool> ImportTorrentMovieAsync(
        Guid libraryId,
        MediaSource source,
        ParsedName parsed,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var movie = new Movie(libraryId, parsed.Title, now);
        movie.SetYear(parsed.Year);
        source.OwnedByMovie(movie.Id);
        movie.AddSource(source);
        await uow.Media.AddAsync(movie, ct);
        await uow.SaveChangesAsync(ct);
        await externalHistoryPromoter.PromoteForMovieAsync(movie, ct);
        return true;
    }

    private async Task<bool> ImportTorrentEpisodeAsync(
        Guid libraryId,
        MediaSource source,
        ParsedName parsed,
        Dictionary<string, Series> cache,
        CancellationToken ct)
    {
        // Reuse episode import shape with a pre-built source (no ffprobe).
        var now = clock.GetUtcNow();
        var seasonNumber = parsed.Season ?? 1;
        var episodeNumber = parsed.Episode ?? 1;

        var seriesIsNew = false;
        if (!cache.TryGetValue(parsed.Title, out var series))
        {
            series = await uow.Media.FindSeriesForScanAsync(libraryId, parsed.Title, ct);
            if (series is null)
            {
                series = new Series(libraryId, parsed.Title, now);
                series.SetYear(parsed.Year);
                await uow.Media.AddAsync(series, ct);
                seriesIsNew = true;
            }

            cache[parsed.Title] = series;
        }

        var season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        var seasonIsNew = season is null;
        if (season is null)
        {
            season = new Season(series.Id, seasonNumber);
            series.AddSeason(season);
        }

        if (!seriesIsNew)
        {
            var existingEpisode = await uow.Media.FindEpisodeForScanAsync(series.Id, seasonNumber, episodeNumber, ct);
            if (existingEpisode is not null)
            {
                source.OwnedByEpisode(existingEpisode.Id);
                await uow.Media.AddMediaSourceAsync(source, ct);
                await uow.SaveChangesAsync(ct);
                await externalHistoryPromoter.PromoteForEpisodeAsync(existingEpisode, series, ct);
                return true;
            }
        }
        else
        {
            var cached = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == episodeNumber);
            if (cached is not null)
                return false;
        }

        var episode = new Episode(series.Id, season.Id, seasonNumber, episodeNumber, now);
        source.OwnedByEpisode(episode.Id);
        episode.AddSource(source);
        season.AddEpisode(episode);

        if (!seriesIsNew)
        {
            if (seasonIsNew)
                await uow.Media.AddSeasonAsync(season, ct);
            else
                await uow.Media.AddEpisodeAsync(episode, ct);
        }

        await uow.SaveChangesAsync(ct);
        await externalHistoryPromoter.PromoteForEpisodeAsync(episode, series, ct);
        return true;
    }

    private static bool IsVideoTorrentEntry(TorrentFileEntry entry) =>
        VideoExtensions.Contains(Path.GetExtension(entry.Path));

    private static bool IsSampleOrTrailer(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        if (string.IsNullOrEmpty(name))
            return false;
        // Tiny samples often named *sample*; also skip common trailer markers.
        foreach (var marker in SampleNameMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateTorrentFiles(IEnumerable<string> roots)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.torrent", options))
                yield return file;
        }
    }

    private async Task<bool> ImportMovieAsync(Guid libraryId, string file, ParsedName parsed, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var movie = new Movie(libraryId, parsed.Title, now);
        movie.SetYear(parsed.Year);

        var source = await BuildSourceAsync(file, ct);
        source.OwnedByMovie(movie.Id);
        movie.AddSource(source);

        await uow.Media.AddAsync(movie, ct);
        await uow.SaveChangesAsync(ct);
        // Title-only promote; id-based promote runs again after metadata enrich.
        await externalHistoryPromoter.PromoteForMovieAsync(movie, ct);
        return true;
    }

    private async Task<bool> ImportEpisodeAsync(
        Guid libraryId,
        string file,
        ParsedName parsed,
        Dictionary<string, Series> cache,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var seasonNumber = parsed.Season ?? 1;
        var episodeNumber = parsed.Episode ?? 1;

        // Reuse an existing series: first from this scan's cache, then from the DB (idempotent
        // re-scans), otherwise create a brand-new one.
        var seriesIsNew = false;
        if (!cache.TryGetValue(parsed.Title, out var series))
        {
            series = await uow.Media.FindSeriesForScanAsync(libraryId, parsed.Title, ct);
            if (series is null)
            {
                series = new Series(libraryId, parsed.Title, now);
                series.SetYear(parsed.Year);
                await uow.Media.AddAsync(series, ct);
                seriesIsNew = true;
            }

            cache[parsed.Title] = series;
        }

        var season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        var seasonIsNew = season is null;
        if (season is null)
        {
            season = new Season(series.Id, seasonNumber);
            series.AddSeason(season);
        }

        // Prefer a DB lookup over navigation collections — after DiscardChanges / concurrent
        // scans the in-memory Episodes list can be stale or empty even when the row exists.
        if (!seriesIsNew)
        {
            var existingEpisode = await uow.Media.FindEpisodeForScanAsync(series.Id, seasonNumber, episodeNumber, ct);
            if (existingEpisode is not null)
            {
                var moved = await BuildSourceAsync(file, ct);
                moved.OwnedByEpisode(existingEpisode.Id);
                await uow.Media.AddMediaSourceAsync(moved, ct);
                await uow.SaveChangesAsync(ct);
                await externalHistoryPromoter.PromoteForEpisodeAsync(existingEpisode, series, ct);
                return true;
            }
        }
        else
        {
            var cached = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == episodeNumber);
            if (cached is not null)
                return false;
        }

        var episode = new Episode(series.Id, season.Id, seasonNumber, episodeNumber, now);
        var source = await BuildSourceAsync(file, ct);
        source.OwnedByEpisode(episode.Id);
        episode.AddSource(source);
        season.AddEpisode(episode);

        // When the series already exists (tracked/loaded), EF would misclassify the newly
        // reachable season/episode as an UPDATE because their keys are client-generated. Adding
        // them explicitly marks the new sub-graph as INSERT. A brand-new series inserts its
        // whole graph via the single AddAsync above.
        if (!seriesIsNew)
        {
            if (seasonIsNew)
                await uow.Media.AddSeasonAsync(season, ct);
            else
                await uow.Media.AddEpisodeAsync(episode, ct);
        }

        await uow.SaveChangesAsync(ct);
        await externalHistoryPromoter.PromoteForEpisodeAsync(episode, series, ct);
        return true;
    }

    private async Task<MediaSource> BuildSourceAsync(string file, CancellationToken ct)
    {
        var info = new FileInfo(file);
        var container = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        var mtime = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : clock.GetUtcNow();
        var size = info.Exists ? info.Length : 0;

        var source = new MediaSource(file, container, size, mtime, clock.GetUtcNow());

        var probe = await ffprobe.ProbeAsync(file, ct);
        if (probe is not null)
        {
            source.SetProbeInfo(probe.DurationMs, probe.OverallBitrateKbps);
            foreach (var stream in probe.Streams)
                source.AddStream(stream);
        }
        else
        {
            // Minimal fallback stream so playback decisions still have something to work with.
            // Leave Codec empty — clients treat "unknown" as a placeholder and hide it.
            source.AddStream(new MediaStream(StreamKind.Video, 0));
        }

        AttachExternalSubtitles(source, file);
        return source;
    }

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt", ".ass", ".ssa",
    };

    private static void AttachExternalSubtitles(MediaSource source, string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
            return;

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(dir, stem + "*");
        }
        catch
        {
            return;
        }

        var nextIndex = source.Streams.Count == 0
            ? 1000
            : source.Streams.Max(s => s.StreamIndex) + 1;

        foreach (var candidate in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(candidate);
            if (!SubtitleExtensions.Contains(ext))
                continue;
            if (string.Equals(candidate, videoPath, StringComparison.Ordinal))
                continue;

            var format = ext.TrimStart('.').ToLowerInvariant();
            var lang = GuessSubtitleLanguage(stem, Path.GetFileNameWithoutExtension(candidate));
            source.AddStream(new MediaStream(StreamKind.Subtitle, nextIndex++)
            {
                Codec = format,
                SubtitleFormat = format,
                IsExternal = true,
                ExternalPath = candidate,
                Language = lang,
            });
        }
    }

    /// <summary>
    /// Re-probe existing sources that lack track titles so dubbing-studio names appear after
    /// upgrading from builds that only mapped <c>tags.language</c>. Cheap no-op once titles exist.
    /// </summary>
    private async Task RefreshStreamTagsIfNeededAsync(string file, CancellationToken ct)
    {
        var source = await uow.Media.GetTrackedSourceByPathWithStreamsAsync(file, ct);
        if (source is null)
            return;

        var needsRefresh = source.Streams.Any(s =>
            (s.Kind == StreamKind.Audio || s.Kind == StreamKind.Subtitle)
            && string.IsNullOrWhiteSpace(s.Title));
        if (!needsRefresh)
            return;

        var probe = await ffprobe.ProbeAsync(file, ct);
        if (probe is null)
            return;

        var changed = false;
        foreach (var probed in probe.Streams)
        {
            if (probed.Kind is not (StreamKind.Audio or StreamKind.Subtitle))
                continue;

            var existing = source.Streams.FirstOrDefault(s =>
                s.StreamIndex == probed.StreamIndex && s.Kind == probed.Kind);
            if (existing is null)
                continue;

            if (!string.Equals(existing.Title, probed.Title, StringComparison.Ordinal)
                || !string.Equals(existing.Language, probed.Language, StringComparison.Ordinal)
                || existing.IsDefault != probed.IsDefault
                || existing.IsForced != probed.IsForced)
            {
                existing.Title = probed.Title;
                if (!string.IsNullOrWhiteSpace(probed.Language))
                    existing.Language = probed.Language;
                existing.IsDefault = probed.IsDefault;
                existing.IsForced = probed.IsForced;
                changed = true;
            }
        }

        if (changed)
            await uow.SaveChangesAsync(ct);
    }

    /// <summary>
    /// movie.en.srt / movie.rus.srt → language tag from the suffix after the video stem.
    /// </summary>
    private static string? GuessSubtitleLanguage(string videoStem, string subtitleStem)
    {
        if (!subtitleStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = subtitleStem[videoStem.Length..].TrimStart('.', '-', '_');
        if (string.IsNullOrWhiteSpace(rest))
            return null;
        // Take first token (en, rus, eng, …).
        var token = rest.Split(['.', '-', '_'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Length is >= 2 and <= 8 ? token.ToLowerInvariant() : null;
    }

    private static IEnumerable<string> EnumerateVideoFiles(IEnumerable<string> roots)
    {
        // IgnoreInaccessible: one permission-denied subdirectory (common on NAS/Docker mounts)
        // must not abort the whole scan. Enumeration exceptions surface during iteration,
        // not when the enumerable is created, so a try around EnumerateFiles alone is useless.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Keep parity with the previous SearchOption.AllDirectories behavior,
            // which did not skip hidden/system entries.
            AttributesToSkip = 0,
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
        }
    }
}
