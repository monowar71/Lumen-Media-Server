using LumenMedia.Api.Auth;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1/home")]
public sealed class HomeController(HomeService home) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<HomeResponse>> Get(CancellationToken ct) =>
        Ok(await home.GetAsync(User.ToCaller(), ct));
}
