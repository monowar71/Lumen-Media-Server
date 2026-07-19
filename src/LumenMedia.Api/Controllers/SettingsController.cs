using LumenMedia.Application.Contracts;
using LumenMedia.Application.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = "Admin")]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<ServerSettingsDto> Get() => Ok(settings.Get());

    [HttpPut]
    public async Task<ActionResult<ServerSettingsDto>> Update(
        [FromBody] ServerSettingsDto patch,
        CancellationToken ct) =>
        Ok(await settings.UpdateAsync(patch, ct));
}
