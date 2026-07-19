using FreePlex.Api.Auth;
using FreePlex.Application.Contracts;
using FreePlex.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "Admin")]
public sealed class UsersController(UserService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> List(CancellationToken ct) =>
        Ok(await users.ListAsync(ct));

    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var user = await users.CreateAsync(request, ct);
        return CreatedAtAction(nameof(List), new { id = user.Id }, user);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct) =>
        Ok(await users.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await users.DeleteAsync(id, User.GetUserId(), ct);
        return NoContent();
    }
}
