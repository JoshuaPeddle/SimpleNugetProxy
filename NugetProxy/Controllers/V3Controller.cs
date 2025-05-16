using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace NugetProxy.Controllers
{
    [ApiController]
    [Route("v3")]
    public class V3Controller : ControllerBase
    {
        private string UpstreamBase;
        private string CacheRoot;

        public V3Controller(IConfiguration configuration)
        {
            CacheRoot = Path.Combine(configuration["CacheRoot"] ?? "nuget-cache", "v3");
            UpstreamBase = configuration["UpstreamBase"] ?? "https://api.nuget.org/v3/flatcontainer";
        }

        [HttpGet("index.json")] 
        public IResult GetIndex()
        {
            var feedBase = $"{Request.Scheme}://{Request.Host}";

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
        }

        [HttpGet("flatcontainer/{**path}")] 
        public async Task<IActionResult> ProxyGetPackage(string path)
        {
            string cachePath = GetCachePath(path);

            if (System.IO.File.Exists(cachePath))
            {
                var stream = System.IO.File.OpenRead(cachePath);
                return File(stream, "application/octet-stream"); 
            }
            else
            {
                string upstreamUrl = $"{UpstreamBase}/{path}";
                using var client = new HttpClient();

                if (Request.Headers.TryGetValue("Authorization", out var authHeaderValue))
                {
                    if (AuthenticationHeaderValue.TryParse(authHeaderValue, out var parsedAuthHeader))
                    {
                        client.DefaultRequestHeaders.Authorization = parsedAuthHeader;
                    }
                }

                try
                {
                    var response = await client.GetAsync(upstreamUrl);
                    if (!response.IsSuccessStatusCode)
                        return StatusCode((int)response.StatusCode);

                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await using (var writeFs = System.IO.File.Create(cachePath))
                    {
                        await response.Content.CopyToAsync(writeFs);
                    }

                    var readFs = System.IO.File.OpenRead(cachePath);
                    return File(readFs, "application/octet-stream");
                }
                catch (HttpRequestException ex)
                {
                    return Problem($"Error connecting to upstream feed: {ex.Message}", statusCode: 502, title: "Bad Gateway");
                }
                catch
                {
                    return NotFound(); 
                }
            }
        }

        private string GetCachePath(string path) => Path.Combine(CacheRoot, path.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));
    }
}