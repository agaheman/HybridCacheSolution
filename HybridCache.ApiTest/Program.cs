using HybridCache.ApiTest.Models;
using HybridCache.Extensions;
using HybridCache.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Infrastructure ────────────────────────────────────────────────────────────

builder.Services.AddMemoryCache();

// Fix: Redis connection string sourced from configuration, not hardcoded
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis connection string 'Redis' is not configured.");

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnectionString));

// ── HybridCache ───────────────────────────────────────────────────────────────

builder.Services.Configure<BrdpHybridCacheOptions>(
    builder.Configuration.GetSection("HybridCache"));

// Registers: IHybridCache<T>, IHybridCacheSerializer<T>, InvalidationListener, OptionsValidator
builder.Services.AddHybridCache<CacheToken>();

// ── API ───────────────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.Run();
