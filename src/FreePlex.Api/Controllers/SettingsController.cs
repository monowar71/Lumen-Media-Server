using FreePlex.Application.Contracts;
using FreePlex.Application.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = "Admin")]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<ServerSettingsDto> Get() => Ok(settings.Get());

    [HttpPut]
    public ActionResult<ServerSettingsDto> Update([FromBody] ServerSettingsDto patch) => Ok(settings.Update(patch));
}
