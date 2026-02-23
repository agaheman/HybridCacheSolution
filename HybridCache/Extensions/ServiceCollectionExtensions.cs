using HybridCache.Abstractions;
using HybridCache.Options;
using HybridCache.Serializers;
using HybridCache.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HybridCache.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HybridCache infrastructure for the given type <typeparamref name="T"/>.
    /// No domain interface required — types stay clean.
    /// </summary>
    public static IServiceCollection AddHybridCache<T>(this IServiceCollection services)
        where T : class
    {
        services.AddSingleton<IValidateOptions<BrdpHybridCacheOptions>, BrdpHybridCacheOptionsValidator>();

        services.AddSingleton<IHybridCacheSerializer<T>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BrdpHybridCacheOptions>>().Value;
            return opts.UseBinarySerializer
                ? new MessagePackHybridCacheSerializer<T>()
                : new JsonHybridCacheSerializer<T>();
        });

        services.AddSingleton<IHybridCache<T>, Services.HybridCache<T>>();
        services.AddHostedService<HybridCacheInvalidationListener>();

        return services;
    }
}
