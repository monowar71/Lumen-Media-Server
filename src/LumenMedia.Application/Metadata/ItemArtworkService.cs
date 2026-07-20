using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Metadata;

/// <summary>Lists alternative covers from metadata providers and applies a chosen URL.</summary>
public sealed class ItemArtworkService(
    IUnitOfWork uow,
    IEnumerable<IMetadataProvider> providers,
    IMetadataLanguageSource languageSource,
    IRemoteImageFetcher images,
    IArtworkStore artworkStore,
    TimeProvider clock)
{
    private const int MaxCandidates = 30;
    private const int MaxArtworkBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.tmdb.org",
    };

    public async Task<IReadOnlyList<ArtworkCandidateDto>> ListCandidatesAsync(
        Guid itemId,
        ArtworkKind kind,
        CancellationToken ct)
    {
        EnsureSupportedKind(kind);

        var item = await uow.Media.GetByIdAsync(itemId, ct)
                   ?? throw new NotFoundException("Item not found.");

        var language = await ResolveLanguageAsync(item.LibraryId, ct);
        var preferredLang = ShortLang(language.Language);
        var fallbackLang = ShortLang(language.FallbackLanguage);

        var results = new List<ArtworkCandidateDto>();
        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            var providerId = ResolveProviderId(item, provider.Name);
            if (string.IsNullOrWhiteSpace(providerId))
                continue;

            var imagesList = await provider.ListArtworkAsync(providerId, item.Kind, kind, language, ct);
            foreach (var img in imagesList)
            {
                results.Add(new ArtworkCandidateDto
                {
                    Provider = img.Provider,
                    Kind = img.Kind.ToString(),
                    Url = img.Url,
                    ThumbnailUrl = img.ThumbnailUrl,
                    Language = img.Language,
                    Width = img.Width,
                    Height = img.Height,
                    VoteAverage = img.VoteAverage,
                });
            }
        }

        return results
            .GroupBy(c => c.Url, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(c => LanguageRank(c.Language, preferredLang, fallbackLang))
            .ThenByDescending(c => c.VoteAverage ?? 0)
            .ThenByDescending(c => c.Width ?? 0)
            .Take(MaxCandidates)
            .ToList();
    }

    public async Task SetAsync(Guid itemId, ArtworkKind kind, string url, CancellationToken ct)
    {
        EnsureSupportedKind(kind);
        var safeUrl = ValidateRemoteUrl(url);

        var item = await uow.Media.GetTrackedForMetadataAsync(itemId, ct)
                   ?? throw new NotFoundException("Item not found.");

        await ApplyFromUrlAsync(item, kind, safeUrl, ct);
        item.Touch(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
    }

    private async Task ApplyFromUrlAsync(MediaItem item, ArtworkKind kind, string url, CancellationToken ct)
    {
        await using var stream = await images.OpenReadAsync(url, ct);
        await using var buffer = new BoundedMemoryStream(MaxArtworkBytes);
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
        await uow.Media.AddArtworkAsync(art, ct);
    }

    private static void EnsureSupportedKind(ArtworkKind kind)
    {
        if (kind is not (ArtworkKind.Poster or ArtworkKind.Backdrop))
            throw new ValidationException("kind", "Only Poster and Backdrop can be changed.");
    }

    private static string ValidateRemoteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !AllowedHosts.Contains(uri.Host))
        {
            throw new ValidationException("url", "Artwork URL host is not allowed.");
        }

        return uri.ToString();
    }

    private static string? ResolveProviderId(MediaItem item, string providerName) =>
        providerName switch
        {
            "Tmdb" => item.TmdbId,
            "Tvdb" => item.TvdbId,
            _ => null,
        };

    private static int LanguageRank(string? language, string preferred, string fallback)
    {
        if (string.IsNullOrWhiteSpace(language))
            return 2; // textless / international — usually good for posters
        var shortLang = ShortLang(language);
        if (string.Equals(shortLang, preferred, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (string.Equals(shortLang, fallback, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }

    private static string ShortLang(string language)
    {
        var dash = language.IndexOf('-');
        return dash > 0 ? language[..dash] : language;
    }

    private async Task<MetadataLanguage> ResolveLanguageAsync(Guid libraryId, CancellationToken ct)
    {
        var server = languageSource.Get();
        var library = await uow.Libraries.GetByIdAsync(libraryId, ct);
        if (library is not null && !string.IsNullOrWhiteSpace(library.PreferredLanguage))
            return server with { Language = library.PreferredLanguage };
        return server;
    }

    private sealed class BoundedMemoryStream(int maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityAllowed(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityAllowed(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacityAllowed(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void EnsureCapacityAllowed(int incoming)
        {
            if (Length + incoming > maxBytes)
                throw new InvalidOperationException($"Artwork exceeds {maxBytes} bytes.");
        }
    }
}
