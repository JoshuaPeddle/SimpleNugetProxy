namespace NugetProxy
{
    public class ProxyOptions
    {
        /// <summary>The upstream NuGet v3 flat container, e.g. https://api.nuget.org/v3/flatcontainer/.</summary>
        public string UpstreamBase { get; set; } = "https://api.nuget.org/v3/flatcontainer/";

        /// <summary>Where package files are kept. Package files never change, so nothing here expires by age.</summary>
        public string CacheRoot { get; set; } = "nuget-cache";

        /// <summary>When the cache passes this size, the least recently used files are removed.</summary>
        public long MaxCacheSizeMB { get; set; } = 500;

        /// <summary>
        /// The address clients use to reach this proxy, for the service index, e.g. https://nuget-cache.example.com.
        /// Needed behind a reverse proxy that terminates TLS; otherwise the request's own scheme and host are used.
        /// </summary>
        public string? PublicBaseUrl { get; set; }

        /// <summary>
        /// Ask the upstream whether a caller may read a package before serving it from the cache, so a cached
        /// private package is never handed to someone the upstream would refuse. Turn off for a public
        /// upstream such as nuget.org, where there is nothing to protect.
        /// </summary>
        public bool ValidateCredentials { get; set; } = true;

        /// <summary>How long an "allowed" answer from the upstream is reused for the same credentials and package.</summary>
        public int AuthCacheSeconds { get; set; } = 300;

        /// <summary>
        /// While the upstream is unreachable, a caller it allowed within this window is still served from the
        /// cache, so an upstream outage does not stop builds that only need cached packages.
        /// </summary>
        public int AuthStaleSeconds { get; set; } = 86400;

        /// <summary>
        /// The upstream base with a trailing slash. Without one, a relative request replaces the last
        /// segment: .../v3/flatcontainer + pkg/index.json would resolve to .../v3/pkg/index.json.
        /// </summary>
        public Uri UpstreamBaseUri => new(UpstreamBase.EndsWith('/') ? UpstreamBase : UpstreamBase + "/");
    }
}
