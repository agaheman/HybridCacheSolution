using Microsoft.Extensions.Options;

namespace HybridCache.Options;

/// <summary>
/// Validates <see cref="BrdpHybridCacheOptions"/> at startup via IValidateOptions,
/// preventing silent misconfiguration (e.g. empty KeyPrefix producing ":{id}" Redis keys).
/// </summary>
internal sealed class BrdpHybridCacheOptionsValidator : IValidateOptions<BrdpHybridCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, BrdpHybridCacheOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            errors.Add($"{nameof(BrdpHybridCacheOptions.KeyPrefix)} must not be null or empty.");

        if (options.MemoryTtl <= TimeSpan.Zero)
            errors.Add($"{nameof(BrdpHybridCacheOptions.MemoryTtl)} must be a positive duration.");

        if (options.RedisTtl <= TimeSpan.Zero)
            errors.Add($"{nameof(BrdpHybridCacheOptions.RedisTtl)} must be a positive duration.");

        if (options.MemoryTtl > options.RedisTtl)
            errors.Add($"{nameof(BrdpHybridCacheOptions.MemoryTtl)} must not exceed {nameof(BrdpHybridCacheOptions.RedisTtl)}.");

        if (string.IsNullOrWhiteSpace(options.InvalidationChannel))
            errors.Add($"{nameof(BrdpHybridCacheOptions.InvalidationChannel)} must not be null or empty.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
