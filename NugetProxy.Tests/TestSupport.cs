using System.Net;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// An upstream feed in memory: files, an optional required Authorization header, and an outage switch.
/// It counts what the proxy asks for, so a test can tell a cache hit from a trip to the upstream.
/// </summary>
public sealed class FakeUpstream : IUpstreamProxyClient
{
    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>When set, callers must send exactly this Authorization header or get 401.</summary>
    public string? RequiredAuthorization { get; set; }

    public bool Down { get; set; }

    public int Gets { get; private set; }
    public int Heads { get; private set; }

    private HttpStatusCode Decide(string path, string? authorization)
    {
        if (Down)
        {
            throw new HttpRequestException("upstream is down");
        }

        if (RequiredAuthorization != null && authorization != RequiredAuthorization)
        {
            return HttpStatusCode.Unauthorized;
        }

        return Files.ContainsKey(path) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
    }

    public Task<HttpResponseMessage> GetAsync(string relativePath, string? authorization, CancellationToken cancellationToken)
    {
        Gets++;
        var status = Decide(relativePath, authorization);
        var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.OK)
        {
            response.Content = new ByteArrayContent(Files[relativePath]);
        }
        else if (status == HttpStatusCode.Unauthorized)
        {
            response.Headers.WwwAuthenticate.Add(new("Basic", "realm=\"fake-upstream\""));
        }

        return Task.FromResult(response);
    }

    public Task<HttpStatusCode> HeadAsync(string relativePath, string? authorization, CancellationToken cancellationToken)
    {
        Heads++;
        return Task.FromResult(Decide(relativePath, authorization));
    }
}
