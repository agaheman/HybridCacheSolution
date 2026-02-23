using HybridCache.Abstractions;
using HybridCache.ApiTest.Models;
using Microsoft.AspNetCore.Mvc;

namespace HybridCache.ApiTest.Controllers;

[ApiController]
[Route("api/cache")]
public sealed class CacheController : ControllerBase
{
    private readonly IHybridCache<CacheToken> _cache;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        IHybridCache<CacheToken> cache,
        ILogger<CacheController> logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    [HttpGet("ping")]
    public IActionResult Ping() => Ok("HybridCache API Running (.NET 9)");

    [HttpPost("{id}")]
    public async Task<IActionResult> Set(string id, [FromBody] string value)
    {
        await _cache.SetAsync(id, new CacheToken { Value = value });
        return Ok("Stored");
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var result = await _cache.GetAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        await _cache.RemoveAsync(id);
        return Ok("Removed");
    }

    /// <summary>
    /// Stress-tests concurrent writes to the same key.
    /// Fix #10: ValueTask cannot be passed to Task.WhenAll directly — must call .AsTask().
    /// </summary>
    [HttpPost("{id}/concurrent")]
    public async Task<IActionResult> ConcurrentTest(string id)
    {
        var tasks = Enumerable
            .Range(0, 10)
            .Select(i => _cache.SetAsync(id, new CacheToken { Value = $"V{i}" }).AsTask()); // Fix #10

        await Task.WhenAll(tasks);

        var final = await _cache.GetAsync(id);
        return Ok(final);
    }
}
