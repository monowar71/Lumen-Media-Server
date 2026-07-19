using FreePlex.Api.Auth;
using FreePlex.Application.Contracts;
using FreePlex.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("setup")]
    [AllowAnonymous]
    [ProducesResponseType<SetupResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SetupResponse>> Setup([FromBody] SetupRequest request, CancellationToken ct)
    {
        var result = await auth.SetupAsync(request, ct);
        return CreatedAtAction(nameof(Me), null, result);
    }

    [HttpPost("auth/login")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        Ok(await auth.LoginAsync(request, ct));

    [HttpPost("auth/refresh")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct) =>
        Ok(await auth.RefreshAsync(request, ct));

    [HttpPost("auth/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken ct)
    {
        await auth.LogoutAsync(User.GetUserId(), request?.RefreshToken, ct);
        return NoContent();
    }

    [HttpGet("auth/me")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct) =>
        Ok(await auth.GetMeAsync(User.GetUserId(), ct));
}
