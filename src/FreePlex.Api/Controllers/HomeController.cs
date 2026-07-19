using FreePlex.Api.Auth;
using FreePlex.Application.Contracts;
using FreePlex.Application.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1/home")]
public sealed class HomeController(HomeService home) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<HomeResponse>> Get(CancellationToken ct) =>
        Ok(await home.GetAsync(User.ToCaller(), ct));
}
