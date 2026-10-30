using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Services
{
    public readonly record struct AuthDecision(bool Allowed, HttpStatusCode Status)
    {
        public static AuthDecision Allow { get; } = new(true, HttpStatusCode.OK);
    }

    /// <summary>
    /// Decides whether a caller may be handed a file that is already in the cache, by asking the upstream.
    ///
    /// The cache is shared by everyone who can reach the proxy, but the upstream's permissions are per
    /// caller. Without this check one authenticated download would make a private package public.
    /// Only "allowed" answers are remembered, and only for the same credentials and package id, so a
    /// refusal or a revoked login takes effect on the next request after AuthCacheSeconds.
    /// </summary>
    public sealed class UpstreamAuthorizer
    {
        private const int MaxRemembered = 20000;

        private readonly IUpstreamProxyClient _upstream;
        private readonly ProxyOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<UpstreamAuthorizer> _logger;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAllowed = new();

        public UpstreamAuthorizer(IUpstreamProxyClient upstream, IOptions<ProxyOptions> options, TimeProvider time, ILogger<UpstreamAuthorizer> logger)
        {
            _upstream = upstream;
            _options = options.Value;
            _time = time;
            _logger = logger;
        }

        // The credential is hashed so it is never kept in memory in the clear.
        private static string Key(PackagePath path, string? authorization)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization ?? string.Empty));
            return Convert.ToHexString(hash) + "|" + path.Id;
        }

        /// <summary>Records that the upstream just served this caller the package, e.g. on a cache miss.</summary>
        public void MarkAllowed(PackagePath path, string? authorization)
        {
            if (_lastAllowed.Count >= MaxRemembered)
            {
                Prune();
            }

            _lastAllowed[Key(path, authorization)] = _time.GetUtcNow();
        }

        public async Task<AuthDecision> CheckAsync(PackagePath path, string? authorization, CancellationToken cancellationToken)
        {
            if (!_options.ValidateCredentials)
            {
                return AuthDecision.Allow;
            }

            var key = Key(path, authorization);
            var now = _time.GetUtcNow();
            var known = _lastAllowed.TryGetValue(key, out var lastAllowed);

            if (known && now - lastAllowed < TimeSpan.FromSeconds(_options.AuthCacheSeconds))
            {
                return AuthDecision.Allow;
            }

            HttpStatusCode status;
            try
            {
                status = await _upstream.HeadAsync(path.RelativePath, authorization, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return StaleDecision(path, known, now - lastAllowed, ex.Message);
            }

            if ((int)status >= 500)
            {
                return StaleDecision(path, known, now - lastAllowed, $"upstream answered {(int)status}");
            }

            if (status == HttpStatusCode.OK)
            {
                _lastAllowed[key] = now;
                return AuthDecision.Allow;
            }

            _lastAllowed.TryRemove(key, out _);
            return new AuthDecision(false, status);
        }

        private AuthDecision StaleDecision(PackagePath path, bool known, TimeSpan age, string reason)
        {
            if (known && age < TimeSpan.FromSeconds(_options.AuthStaleSeconds))
            {
                _logger.LogWarning("Upstream unavailable ({Reason}); serving {Path} from the cache to a caller allowed {Age:N0}s ago", reason, path.RelativePath, age.TotalSeconds);
                return AuthDecision.Allow;
            }

            _logger.LogWarning("Upstream unavailable ({Reason}) and no recent permission for {Path}; refusing", reason, path.RelativePath);
            return new AuthDecision(false, HttpStatusCode.BadGateway);
        }

        private void Prune()
        {
            var oldest = _time.GetUtcNow() - TimeSpan.FromSeconds(_options.AuthStaleSeconds);
            foreach (var pair in _lastAllowed)
            {
                if (pair.Value < oldest)
                {
                    _lastAllowed.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
