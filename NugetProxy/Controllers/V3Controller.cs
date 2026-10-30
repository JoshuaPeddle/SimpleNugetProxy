using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NugetProxy.Services;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Controllers
{
    [ApiController]
    [Route("v3")]
    public class V3Controller : ControllerBase
    {
        private const string BasicChallenge = "Basic realm=\"NugetProxy\"";

        private readonly ICacheStorageService _cache;
        private readonly IUpstreamProxyClient _upstream;
        private readonly UpstreamAuthorizer _authorizer;
        private readonly KeyedLock _locks;
        private readonly ProxyOptions _options;
        private readonly ILogger<V3Controller> _logger;

        public V3Controller(
            ICacheStorageService cache,
            IUpstreamProxyClient upstream,
            UpstreamAuthorizer authorizer,
            KeyedLock locks,
            IOptions<ProxyOptions> options,
            ILogger<V3Controller> logger)
        {
            _cache = cache;
            _upstream = upstream;
            _authorizer = authorizer;
            _locks = locks;
            _options = options.Value;
            _logger = logger;
        }

        [HttpGet("index.json")]
        public IResult GetIndex()
        {
            var feedBase = string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
                ? $"{Request.Scheme}://{Request.Host}"
                : _options.PublicBaseUrl.TrimEnd('/');

            var index = new Dictionary<string, object>
            {
                ["version"] = "3.0.0",
                ["resources"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["@id"] = $"{feedBase}/v3/flatcontainer/",
                        ["@type"] = "PackageBaseAddress/3.0.0",
                        ["comment"] = "Base URL for cached packages"
                    }
                }
            };

            return Results.Json(index, contentType: "application/json");
        }

        [HttpGet("flatcontainer/{**path}")]
        public async Task<IActionResult> GetFlatContainer(string path, CancellationToken cancellationToken)
        {
            if (!PackagePath.TryParse(path, out var package) || package == null)
            {
                return NotFound();
            }

            string? authorization = Request.Headers.Authorization.FirstOrDefault();

            return package.IsVersionList
                ? await GetVersionListAsync(package, authorization, cancellationToken)
                : await GetPackageFileAsync(package, authorization, cancellationToken);
        }

        // Package files never change once published, so a cached copy is served as is, after the
        // upstream has confirmed that this caller may have it.
        private async Task<IActionResult> GetPackageFileAsync(PackagePath package, string? authorization, CancellationToken cancellationToken)
        {
            var hit = await TryServeCachedAsync(package, authorization, cancellationToken);
            if (hit != null)
            {
                return hit;
            }

            // One download per file, however many builds ask for it at once.
            using var gate = await _locks.AcquireAsync(package.RelativePath, cancellationToken);

            hit = await TryServeCachedAsync(package, authorization, cancellationToken);
            if (hit != null)
            {
                return hit;
            }

            HttpResponseMessage response;
            try
            {
                response = await _upstream.GetAsync(package.RelativePath, authorization, cancellationToken);
            }
            catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Upstream unreachable fetching {Path}", package.RelativePath);
                return StatusCode((int)HttpStatusCode.BadGateway);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return UpstreamStatus(response);
                }

                _authorizer.MarkAllowed(package, authorization);
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var stored = await _cache.StoreAsync(package.RelativePath, body, cancellationToken);
                _logger.LogInformation("Cached {Path}", package.RelativePath);
                return File(stored, ContentType(package));
            }
        }

        // The version list changes whenever a version is published, so it always comes from the upstream.
        // The copy kept here is only a fallback for when the upstream is down.
        private async Task<IActionResult> GetVersionListAsync(PackagePath package, string? authorization, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await _upstream.GetAsync(package.RelativePath, authorization, cancellationToken);
            }
            catch (Exception ex) when (IsUpstreamFailure(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Upstream unreachable fetching {Path}", package.RelativePath);
                return await TryServeCachedAsync(package, authorization, cancellationToken) ?? StatusCode((int)HttpStatusCode.BadGateway);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    _authorizer.MarkAllowed(package, authorization);

                    using var content = new MemoryStream(bytes);
                    await using (await _cache.StoreAsync(package.RelativePath, content, cancellationToken))
                    {
                    }

                    return File(bytes, ContentType(package));
                }

                if ((int)response.StatusCode >= 500)
                {
                    return await TryServeCachedAsync(package, authorization, cancellationToken) ?? StatusCode((int)HttpStatusCode.BadGateway);
                }

                return UpstreamStatus(response);
            }
        }

        // Null when the file is not cached. A cached file is only served if the upstream allows this caller.
        private async Task<IActionResult?> TryServeCachedAsync(PackagePath package, string? authorization, CancellationToken cancellationToken)
        {
            var cached = _cache.TryOpenRead(package.RelativePath);
            if (cached == null)
            {
                return null;
            }

            var decision = await _authorizer.CheckAsync(package, authorization, cancellationToken);
            if (!decision.Allowed)
            {
                await cached.DisposeAsync();

                // The upstream could not be asked and this caller sent no login. NuGet sends its credentials
                // only after a 401, so challenge it: with a login it may still be served from the cache.
                var status = decision.Status == HttpStatusCode.BadGateway && authorization == null
                    ? HttpStatusCode.Unauthorized
                    : decision.Status;
                return StatusFor(status);
            }

            return File(cached, ContentType(package));
        }

        private IActionResult UpstreamStatus(HttpResponseMessage response)
        {
            if (response.Headers.WwwAuthenticate.Count > 0)
            {
                Response.Headers.WWWAuthenticate = string.Join(", ", response.Headers.WwwAuthenticate);
            }

            return StatusFor(response.StatusCode);
        }

        // Only statuses a client can act on are passed on; anything else from the upstream is a bad gateway.
        private IActionResult StatusFor(HttpStatusCode status)
        {
            switch (status)
            {
                case HttpStatusCode.Unauthorized:
                    if (!Response.Headers.ContainsKey("WWW-Authenticate"))
                    {
                        Response.Headers.WWWAuthenticate = BasicChallenge;
                    }

                    return StatusCode((int)HttpStatusCode.Unauthorized);
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotFound:
                    return StatusCode((int)status);
                default:
                    return StatusCode((int)HttpStatusCode.BadGateway);
            }
        }

        private static bool IsUpstreamFailure(Exception ex, CancellationToken cancellationToken)
            => ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested;

        private static string ContentType(PackagePath package)
        {
            if (package.IsVersionList)
            {
                return "application/json";
            }

            return package.FileName!.EndsWith(".nuspec", StringComparison.Ordinal) ? "application/xml" : "application/octet-stream";
        }
    }
}
