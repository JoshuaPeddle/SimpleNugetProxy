var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string upstreamBase = "https://api.nuget.org/v3/flatcontainer";
string cacheRoot = "nuget-cache";

app.MapGet("/v3/flatcontainer/{**path}", async (HttpRequest req, string path) => await ProxyGet(req, path));

app.MapGet("/v3/index.json", (HttpContext ctx) =>
{
    var feedBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    var index = new Dictionary<string, object>
    {
        ["version"] = "3.0.0",
        ["resources"] = new[] {
            new Dictionary<string, object>
            {
                ["@id"]   = $"{feedBase}/v3/flatcontainer/",
                ["@type"] = "PackageBaseAddress/3.0.0",
                ["comment"] = "Base URL for cached packages"
            }
        }
    };

    return Results.Json(index, contentType: "application/json");
});

async Task<IResult> ProxyGet(HttpRequest req, string path) 
{
    string cachePath = GetCachePath(path);

    if (File.Exists(cachePath))
    {
        var stream = File.OpenRead(cachePath);
        return Results.File(stream, "application/octet-stream");
    }
    else
    {
        string upstreamUrl = $"{upstreamBase}/{path}";
        using var client = new HttpClient();
        try
        {
            var response = await client.GetAsync(upstreamUrl);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode);

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using (var writeFs = File.Create(cachePath))
            {
                await response.Content.CopyToAsync(writeFs);
            }

            var readFs = File.OpenRead(cachePath);
            return Results.File(readFs, "application/octet-stream");
        }
        catch
        {
            return Results.NotFound();
        }
    }
}
string GetCachePath(string path) => Path.Combine(cacheRoot, path.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));

app.MapGet("/health", () => "OK");

app.Run();
