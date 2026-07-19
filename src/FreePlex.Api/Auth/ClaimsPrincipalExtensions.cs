using System.Security.Claims;
using FreePlex.Application.Common;
using FreePlex.Domain.Enums;

namespace FreePlex.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : throw new UnauthorizedException("Invalid token subject.");
    }

    public static Caller ToCaller(this ClaimsPrincipal principal)
    {
        var id = principal.GetUserId();
        var roleValue = principal.FindFirstValue("role") ?? principal.FindFirstValue(ClaimTypes.Role) ?? "User";
        var role = Enum.TryParse<UserRole>(roleValue, out var r) ? r : UserRole.User;

        var libClaims = principal.FindAll("libs").Select(c => c.Value).ToList();
        var allLibraries = role == UserRole.Admin || libClaims.Contains("*");
        var libraryIds = libClaims
            .Where(v => v != "*")
            .Select(v => Guid.TryParse(v, out var g) ? g : (Guid?)null)
            .Where(g => g is not null)
            .Select(g => g!.Value)
            .ToList();

        return new Caller(id, role, allLibraries, libraryIds);
    }
}
