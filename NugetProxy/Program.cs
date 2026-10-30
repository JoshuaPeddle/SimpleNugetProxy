using System.Net;
using Microsoft.Extensions.Options;
using NugetProxy;
using NugetProxy.Services;
using NugetProxy.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// UpstreamBase, CacheRoot and the other settings sit at the top level of appsettings.json, and each
// can be overridden by an environment variable of the same name (e.g. UpstreamBase=...).
builder.Services.Configure<ProxyOptions>(builder.Configuration);

builder.Services.AddControllers();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICacheStorageService, SizeLimitedFileSystemCacheStorageService>();
builder.Services.AddSingleton<KeyedLock>();
builder.Services.AddSingleton<UpstreamAuthorizer>();

builder.Services.AddHttpClient<IUpstreamProxyClient, HttpUpstreamProxyClient>((services, client) =>
    {
        client.BaseAddress = services.GetRequiredService<IOptions<ProxyOptions>>().Value.UpstreamBaseUri;
        client.Timeout = TimeSpan.FromMinutes(10);
    })
    // Cached files are stored decompressed, so they can be served to any client as they are.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

var app = builder.Build();

// Open the cache now, so a cache path that cannot be written stops the start-up instead of failing the first request.
app.Services.GetRequiredService<ICacheStorageService>();

app.MapControllers();

app.MapGet("/health", () => "OK");
app.Run();

// Lets the test project start the application.
public partial class Program;
