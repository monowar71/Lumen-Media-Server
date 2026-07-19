using FreePlex.Domain.Enums;

namespace FreePlex.Application.Common;

/// <summary>Authenticated caller context derived from JWT claims.</summary>
public sealed record Caller(Guid UserId, UserRole Role, bool AllLibraries, IReadOnlyList<Guid> LibraryIds)
{
    public bool IsAdmin => Role == UserRole.Admin;

    public bool CanAccess(Guid libraryId) => IsAdmin || AllLibraries || LibraryIds.Contains(libraryId);
}
