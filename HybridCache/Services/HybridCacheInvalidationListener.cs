using HybridCache.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HybridCache.Services;

/// <summary>
/// Background service that subscribes to the Redis pub/sub invalidation channel
/// and evicts matching keys from the local L1 <see cref="IMemoryCache"/>.
/// This ensures cross-instance L1 coherence in multi-node deployments.
/// </summary>
internal sealed class HybridCacheInvalidationListener : IHostedService
{
    private readonly IMemoryCache _memory;
    private readonly IConnectionMultiplexer _mux;
    private readonly BrdpHybridCacheOptions _options;
    private readonly ILogger<HybridCacheInvalidationListener> _logger;
    private ISubscriber? _subscriber;

    public HybridCacheInvalidationListener(
        IMemoryCache memory,
        IConnectionMultiplexer mux,
        IOptions<BrdpHybridCacheOptions> options,          // Fix #2: IOptions<T>
        ILogger<HybridCacheInvalidationListener> logger)
    {
        _memory = memory;
        _mux = mux;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscriber = _mux.GetSubscriber();

        // RedisChannel.Literal: exact match, no glob pattern — correct for our named channel.
        _subscriber.Subscribe(
            RedisChannel.Literal(_options.InvalidationChannel),
            (channel, message) =>
            {
                var key = message.ToString();
                _memory.Remove(key);
                _logger.LogDebug("Invalidated L1 key '{Key}' via pub/sub channel '{Channel}'", key, channel);
            });

        _logger.LogInformation(
            "HybridCacheInvalidationListener started on channel '{Channel}'",
            _options.InvalidationChannel);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscriber?.Unsubscribe(RedisChannel.Literal(_options.InvalidationChannel));
        _logger.LogInformation("HybridCacheInvalidationListener stopped.");
        return Task.CompletedTask;
    }
}