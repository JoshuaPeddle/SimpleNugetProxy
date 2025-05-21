using NugetProxy.Services;
using NugetProxy.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.
    AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddControllers();

builder.Services.AddHttpClient("UpstreamBase", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["UpstreamBase"] ?? "https://api.nuget.org/v3/flatcontainer");
});

builder.Services.AddSingleton<ICacheStorageService, SizeLimitedFileSystemCacheStorageService>();
builder.Services.AddScoped<IUpstreamProxyClient, HttpUpstreamProxyClient>();

var app = builder.Build();

app.MapControllers();

app.MapGet("/health", () => "OK");
app.Run();
