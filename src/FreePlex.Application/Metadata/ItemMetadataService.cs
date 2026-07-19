using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Domain.Media;

namespace FreePlex.Application.Metadata;

public sealed class ItemMetadataService(
    IUnitOfWork uow,
    IEnumerable<IMetadataProvider> providers,
    IMetadataLanguageSource languageSource,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<MetadataMatchCandidateDto>> SearchCandidatesAsync(
        Guid itemId,
        string? query,
        int? year,
        CancellationToken ct)
    {
        var item = await uow.Media.GetByIdAsync(itemId, ct)
                   ?? throw new NotFoundException("Item not found.");

        var title = string.IsNullOrWhiteSpace(query) ? item.Title : query.Trim();
        var searchYear = year ?? item.Year;
        var language = await ResolveLanguageAsync(item.LibraryId, ct);

        var results = new List<MetadataMatchCandidateDto>();
        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            var matches = await provider.SearchAsync(title, searchYear, item.Kind, language, ct);
            foreach (var m in matches)
            {
                results.Add(new MetadataMatchCandidateDto
                {
                    Provider = m.Provider,
                    ProviderId = m.ProviderId,
                    Title = m.Title,
                    Year = m.Year,
                    Score = Math.Round(m.Score, 3),
                });
            }
        }

        return results
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
    }

    public async Task UpdateAsync(Guid itemId, UpdateItemMetadataRequest request, CancellationToken ct)
    {
        var item = await uow.Media.GetTrackedForMetadataAsync(itemId, ct)
                   ?? throw new NotFoundException("Item not found.");

        if (request.Title is not null)
            item.SetTitle(request.Title);
        if (request.OriginalTitle is not null)
            item.SetOriginalTitle(string.IsNullOrWhiteSpace(request.OriginalTitle) ? null : request.OriginalTitle.Trim());
        if (request.Year.HasValue)
            item.SetYear(request.Year.Value == 0 ? null : request.Year);
        if (request.Overview is not null)
            item.SetOverview(string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim());
        if (request.CommunityRating.HasValue || request.OfficialRating is not null)
        {
            item.SetRatings(
                request.CommunityRating ?? item.CommunityRating,
                request.OfficialRating is null
                    ? item.OfficialRating
                    : (string.IsNullOrWhiteSpace(request.OfficialRating) ? null : request.OfficialRating.Trim()));
        }

        if (item is Movie movie && request.Tagline is not null)
        {
            movie.SetMovieDetails(
                string.IsNullOrWhiteSpace(request.Tagline) ? null : request.Tagline.Trim(),
                movie.ReleaseDate,
                movie.RuntimeMs);
        }

        if (request.MetadataLocked.HasValue)
            item.SetMetadataLocked(request.MetadataLocked.Value);
        else if (request.Title is not null
                 || request.Overview is not null
                 || request.Year.HasValue
                 || request.OriginalTitle is not null
                 || request.Tagline is not null
                 || request.CommunityRating.HasValue
                 || request.OfficialRating is not null)
        {
            // Manual edits lock by default so the next auto-refresh does not overwrite them.
            item.SetMetadataLocked(true);
        }

        item.Touch(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
    }

    private async Task<MetadataLanguage> ResolveLanguageAsync(Guid libraryId, CancellationToken ct)
    {
        var server = languageSource.Get();
        var library = await uow.Libraries.GetByIdAsync(libraryId, ct);
        if (library is not null && !string.IsNullOrWhiteSpace(library.PreferredLanguage))
            return server with { Language = library.PreferredLanguage };
        return server;
    }
}
