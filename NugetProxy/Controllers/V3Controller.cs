using Microsoft.AspNetCore.Mvc;
using NugetProxy.Services; 

namespace NugetProxy.Controllers
{
    [ApiController]
    [Route("v3")]
    public class V3Controller : ControllerBase 
    {
        private readonly ICacheStorageService _cacheStorageService;
        private readonly IUpstreamProxyClient _upstreamProxyClient;

        public V3Controller(ICacheStorageService cacheStorageService, IUpstreamProxyClient upstreamProxyClient)
        {
            _cacheStorageService = cacheStorageService;
            _upstreamProxyClient = upstreamProxyClient;
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
            string cachePath = _cacheStorageService.GetFullPath(path);

            if (_cacheStorageService.FileExists(cachePath))
            {
                var stream = _cacheStorageService.OpenReadStream(cachePath);
                return File(stream, "application/octet-stream");
            }
            else
            {
                string upstreamUrl = path; 

                try
                {
                    var response = await _upstreamProxyClient.GetAsync(upstreamUrl, Request.Headers);

                    if (!response.IsSuccessStatusCode)
                        return StatusCode((int)response.StatusCode);

                    await using var responseStream = await response.Content.ReadAsStreamAsync();
                    await _cacheStorageService.SaveFileAsync(cachePath, responseStream);

                    var readFs = _cacheStorageService.OpenReadStream(cachePath);
                    return File(readFs, "application/octet-stream");
                }
                catch (HttpRequestException ex)
                {
                    return Problem($"Error connecting to upstream feed: {ex.Message}", statusCode: 502, title: "Bad Gateway");
                }
                catch (IOException ex) 
                {
                    return Problem($"Error accessing cache: {ex.Message}", statusCode: 500, title: "Cache Access Error");
                }
                catch (System.Exception ex) 
                {
                    return Problem($"An unexpected error occurred: {ex.Message}", statusCode: 500, title: "Internal Server Error");
                }
            }
        }
    }
}