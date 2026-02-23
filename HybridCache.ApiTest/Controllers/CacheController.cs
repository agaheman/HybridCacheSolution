using HybridCache.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace HybridCache.ApiTest.Controllers;

[ApiController]
[Route("api/cache")]
public class CacheController : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok("HybridCache API Running (.NET 9)");
}

public sealed class CacheToken : IVersioned
{
    public long Version { get; set; }
    public string Value { get; set; } = default!;
}