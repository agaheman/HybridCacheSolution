using HybridCache.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("10.100.7.56:6379,connectRetry=5"));

builder.Services.Configure<HybridCacheOptions>(builder.Configuration.GetSection("HybridCache"));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.Run();