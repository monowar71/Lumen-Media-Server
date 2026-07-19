using FreePlex.Application.Abstractions;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;
using Microsoft.Extensions.Logging;

namespace FreePlex.Infrastructure.Metadata;

public sealed class HttpRemoteImageFetcher(IHttpClientFactory httpClientFactory) : IRemoteImageFetcher
{
    public async Task<Stream> OpenReadAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("TmdbImages");
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }
}

/// <summary>
/// Matches a library item to TMDB (or an explicit provider id), writes overview/ratings/genres
/// and caches poster/backdrop under /config/metadata.
/// </summary>
public sealed class MetadataEnricher(
    IUnitOfWork uow,
    IEnumerable<IMetadataProvider> providers,
    IArtworkStore artworkStore,
    IRemoteImageFetcher images,
    IMetadataLanguageSource languageSource,
    TimeProvider clock,
    ILogger<MetadataEnricher> logger) : IMetadataEnricher
{
    public async Task<bool> EnrichAsync(Guid itemId, string? provider, string? providerId, CancellationToken ct)
    {
        var item = await uow.Media.GetTrackedForMetadataAsync(itemId, ct);
        if (item is null)
        {
            logger.LogWarning("Metadata enrich skipped: item {ItemId} not found", itemId);
            return false;
        }

        if (item.MetadataLocked && string.IsNullOrEmpty(providerId))
        {
            logger.LogInformation("Metadata locked for {ItemId}; skip auto enrich", itemId);
            return false;
        }

        var language = await ResolveLanguageAsync(item.LibraryId, ct);
        var kind = item.Kind;
        MetadataDetails? details = null;

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var named = ResolveProvider(provider);
            if (named is null)
                return false;
            details = await named.GetDetailsAsync(providerId, kind, language, ct);
        }
        else
        {
            details = await AutoMatchAsync(item, language, ct);
        }

        if (details is null)
        {
            logger.LogInformation("No metadata match for {Title} ({ItemId})", item.Title, itemId);
            return false;
        }

        ApplyDetails(item, details);
        // Explicit rematch replaces locked manual edits; unlock so future refresh works.
        if (!string.IsNullOrWhiteSpace(providerId))
            item.SetMetadataLocked(false);
        item.Touch(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);

        await ApplyGenresAsync(item, details.Genres, ct);
        await uow.SaveChangesAsync(ct);

        await ApplyArtworkAsync(item, ArtworkKind.Poster, details.PosterUrl, ct);
        if (!string.Equals(details.PosterUrl, details.BackdropUrl, StringComparison.Ordinal))
            await ApplyArtworkAsync(item, ArtworkKind.Backdrop, details.BackdropUrl, ct);
        await uow.SaveChangesAsync(ct);

        logger.LogInformation(
            "Enriched {Title} via {Provider}/{ProviderId}",
            item.Title, details.Provider, details.ProviderId);
        return true;
    }

    private async Task<MetadataLanguage> ResolveLanguageAsync(Guid libraryId, CancellationToken ct)
    {
        var server = languageSource.Get();
        var library = await uow.Libraries.GetByIdAsync(libraryId, ct);
        if (library is not null && !string.IsNullOrWhiteSpace(library.PreferredLanguage))
            return server with { Language = library.PreferredLanguage };
        return server;
    }

    private async Task<MetadataDetails?> AutoMatchAsync(MediaItem item, MetadataLanguage language, CancellationToken ct)
    {
        // Prefer an existing external id on re-refresh.
        if (!string.IsNullOrWhiteSpace(item.TmdbId))
        {
            var tmdb = providers.FirstOrDefault(p =>
                p.Name.Equals(TmdbMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
            var existing = tmdb is null ? null : await tmdb.GetDetailsAsync(item.TmdbId, item.Kind, language, ct);
            if (existing is not null)
                return existing;
        }

        if (!string.IsNullOrWhiteSpace(item.TvdbId))
        {
            var tvdb = providers.FirstOrDefault(p =>
                p.Name.Equals(TvdbMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
            if (tvdb is not null)
            {
                var existing = await tvdb.GetDetailsAsync(item.TvdbId, item.Kind, language, ct);
                if (existing is not null)
                    return existing;
            }

            var tvmaze = providers.FirstOrDefault(p =>
                p.Name.Equals(TvMazeMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
            var existingMaze = tvmaze is null
                ? null
                : await tvmaze.GetDetailsAsync(item.TvdbId, item.Kind, language, ct);
            if (existingMaze is not null)
                return existingMaze;
        }

        MetadataMatch? best = null;
        IMetadataProvider? bestProvider = null;
        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            var matches = await provider.SearchAsync(item.Title, item.Year, item.Kind, language, ct);
            var top = matches.FirstOrDefault();
            if (top is null)
                continue;
            if (best is null || top.Score > best.Score)
            {
                best = top;
                bestProvider = provider;
            }
        }

        // 0.70 rejects weak substring-only hits; exact/original_title matches clear it easily.
        if (best is null || bestProvider is null || best.Score < 0.70)
            return null;

        return await bestProvider.GetDetailsAsync(best.ProviderId, item.Kind, language, ct);
    }

    private IMetadataProvider? ResolveProvider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return providers.FirstOrDefault(p => p.IsConfigured);

        return providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyDetails(MediaItem item, MetadataDetails details)
    {
        item.SetTitle(details.Title);
        item.SetOriginalTitle(details.OriginalTitle);
        if (details.Year is not null)
            item.SetYear(details.Year);
        item.SetOverview(details.Overview);
        item.SetRatings(details.CommunityRating, details.OfficialRating);

        if (details.Provider.Equals(TmdbMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            item.SetExternalIds(details.ProviderId, item.TvdbId, details.ImdbId ?? item.ImdbId);
        else if (details.Provider.Equals(TvdbMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            item.SetExternalIds(item.TmdbId, details.ProviderId, details.ImdbId ?? item.ImdbId);
        else
            // TVMaze (and similar) reuse TvdbId slot until a dedicated column exists.
            item.SetExternalIds(item.TmdbId, details.ProviderId, details.ImdbId ?? item.ImdbId);

        if (item is Movie movie)
            movie.SetMovieDetails(details.Tagline, details.ReleaseDate, details.RuntimeMs);
    }

    private async Task ApplyGenresAsync(MediaItem item, IReadOnlyList<string> genres, CancellationToken ct)
    {
        foreach (var name in genres.Take(12))
        {
            var genre = await uow.Media.GetOrCreateGenreAsync(name, ct);
            item.AddGenre(genre);
        }
    }

    private async Task ApplyArtworkAsync(MediaItem item, ArtworkKind kind, string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            await using var stream = await images.OpenReadAsync(url, ct);
            // Buffer to a memory stream so SaveAsync can rewind if needed; posters are small.
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            var path = await artworkStore.SaveAsync(item.Id, kind, buffer, ct);

            foreach (var old in item.Artworks.Where(a => a.Kind == kind).ToList())
                uow.Media.RemoveArtwork(old);
            item.RemoveArtworksOfKind(kind);

            var art = new Artwork(kind, path, mediaItemId: item.Id)
            {
                SourceUrl = url,
                IsPrimary = kind == ArtworkKind.Poster,
            };
            item.AddArtwork(art);
            // Client-generated Guid keys must be Add'd explicitly or EF issues UPDATE … WHERE Id=…
            // against a missing row (DbUpdateConcurrencyException) and posters never appear in the UI.
            await uow.Media.AddArtworkAsync(art, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to download {Kind} for {ItemId} from {Url}", kind, item.Id, url);
        }
    }
}
