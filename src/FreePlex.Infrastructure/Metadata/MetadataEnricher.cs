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

        var kind = item.Kind;
        MetadataDetails? details = null;

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var named = ResolveProvider(provider);
            if (named is null)
                return false;
            details = await named.GetDetailsAsync(providerId, kind, ct);
        }
        else
        {
            details = await AutoMatchAsync(item, ct);
        }

        if (details is null)
        {
            logger.LogInformation("No metadata match for {Title} ({ItemId})", item.Title, itemId);
            return false;
        }

        ApplyDetails(item, details);
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

    private async Task<MetadataDetails?> AutoMatchAsync(MediaItem item, CancellationToken ct)
    {
        // Prefer an existing external id on re-refresh.
        if (!string.IsNullOrWhiteSpace(item.TmdbId))
        {
            var tmdb = providers.FirstOrDefault(p =>
                p.Name.Equals(TmdbMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
            var existing = tmdb is null ? null : await tmdb.GetDetailsAsync(item.TmdbId, item.Kind, ct);
            if (existing is not null)
                return existing;
        }

        if (!string.IsNullOrWhiteSpace(item.TvdbId))
        {
            var tvmaze = providers.FirstOrDefault(p =>
                p.Name.Equals(TvMazeMetadataProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
            var existing = tvmaze is null ? null : await tvmaze.GetDetailsAsync(item.TvdbId, item.Kind, ct);
            if (existing is not null)
                return existing;
        }

        MetadataMatch? best = null;
        IMetadataProvider? bestProvider = null;
        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            var matches = await provider.SearchAsync(item.Title, item.Year, item.Kind, ct);
            var top = matches.FirstOrDefault();
            if (top is null)
                continue;
            if (best is null || top.Score > best.Score)
            {
                best = top;
                bestProvider = provider;
            }
        }

        if (best is null || bestProvider is null || best.Score < 0.55)
            return null;

        return await bestProvider.GetDetailsAsync(best.ProviderId, item.Kind, ct);
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
        else
            // Non-TMDB providers (e.g. TVMaze) reuse TvdbId slot until a dedicated column exists.
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to download {Kind} for {ItemId} from {Url}", kind, item.Id, url);
        }
    }
}
