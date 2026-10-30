using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Tests;

/// <summary>The whole request pipeline against an in-memory upstream.</summary>
public sealed class ProxyEndToEndTests : IDisposable
{
    private const string Nupkg = "townsuite.secret/1.0.0/townsuite.secret.1.0.0.nupkg";
    private const string VersionList = "townsuite.secret/index.json";
    private static readonly string AliceAuth = Basic("alice", "pw-alice");
    private static readonly string BobAuth = Basic("bob", "pw-bob");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nugetproxy-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUpstream _upstream = new();
    private readonly FakeTime _time = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ProxyEndToEndTests()
    {
        _upstream.Files[Nupkg] = Encoding.UTF8.GetBytes("package-bytes");
        _upstream.Files[VersionList] = Encoding.UTF8.GetBytes("{\"versions\":[\"1.0.0\"]}");
        _upstream.RequiredAuthorization = AliceAuth;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CacheRoot", _dir);
            builder.UseSetting("ValidateCredentials", "true");
            builder.UseSetting("AuthCacheSeconds", "300");
            builder.UseSetting("AuthStaleSeconds", "86400");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamProxyClient>();
                services.AddSingleton<IUpstreamProxyClient>(_upstream);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_time);
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Basic(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private Task<HttpResponseMessage> Get(string path, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v3/flatcontainer/" + path);
        if (authorization != null)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        }

        return _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_miss_is_fetched_from_the_upstream_and_kept()
    {
        var first = await Get(Nupkg, AliceAuth);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("package-bytes", await first.Content.ReadAsStringAsync());
        Assert.Equal(1, _upstream.Gets);

        var second = await Get(Nupkg, AliceAuth);
        Assert.Equal("package-bytes", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, _upstream.Gets);
    }

    [Fact]
    public async Task A_cached_private_package_is_refused_to_someone_the_upstream_refuses()
    {
        await Get(Nupkg, AliceAuth);

        var anonymous = await Get(Nupkg);
        var bob = await Get(Nupkg, BobAuth);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bob.StatusCode);
        Assert.Contains("Basic", anonymous.Headers.WwwAuthenticate.ToString());
        Assert.Equal(1, _upstream.Gets);
    }

    [Fact]
    public async Task The_upstream_is_asked_once_per_window_for_the_same_caller()
    {
        await Get(Nupkg, AliceAuth);

        await Get(Nupkg, AliceAuth);
        await Get(Nupkg, AliceAuth);
        Assert.Equal(0, _upstream.Heads);

        _time.Advance(TimeSpan.FromSeconds(301));
        await Get(Nupkg, AliceAuth);
        await Get(Nupkg, AliceAuth);
        Assert.Equal(1, _upstream.Heads);
    }

    [Fact]
    public async Task A_revoked_login_stops_working_after_the_window()
    {
        await Get(Nupkg, AliceAuth);
        _upstream.RequiredAuthorization = "Basic revoked";

        _time.Advance(TimeSpan.FromSeconds(301));
        var after = await Get(Nupkg, AliceAuth);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_refused_caller_is_not_remembered_as_allowed()
    {
        await Get(Nupkg, AliceAuth);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Nupkg, BobAuth)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Nupkg, BobAuth)).StatusCode);
        Assert.Equal(2, _upstream.Heads);
    }

    [Fact]
    public async Task A_new_version_shows_up_at_once_because_version_lists_are_not_served_stale()
    {
        var before = await Get(VersionList, AliceAuth);
        Assert.Contains("1.0.0", await before.Content.ReadAsStringAsync());

        _upstream.Files[VersionList] = Encoding.UTF8.GetBytes("{\"versions\":[\"1.0.0\",\"1.1.0\"]}");
        var after = await Get(VersionList, AliceAuth);

        Assert.Contains("1.1.0", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_lists_keep_working_when_the_upstream_is_down_for_a_caller_it_recently_allowed()
    {
        await Get(VersionList, AliceAuth);
        _upstream.Down = true;

        var response = await Get(VersionList, AliceAuth);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("1.0.0", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cached_packages_keep_working_during_an_outage_for_a_caller_it_recently_allowed()
    {
        await Get(Nupkg, AliceAuth);
        _upstream.Down = true;
        _time.Advance(TimeSpan.FromHours(2));

        var response = await Get(Nupkg, AliceAuth);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("package-bytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_outage_does_not_open_the_cache_to_a_caller_the_upstream_never_allowed()
    {
        await Get(Nupkg, AliceAuth);
        _upstream.Down = true;

        var response = await Get(Nupkg, BobAuth);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task During_an_outage_an_anonymous_caller_is_challenged_so_the_client_sends_its_login()
    {
        // NuGet sends credentials only after a 401, so a 502 here would stop a restore that could have been served.
        await Get(Nupkg, AliceAuth);
        await Get(VersionList, AliceAuth);
        _upstream.Down = true;

        var package = await Get(Nupkg);
        var versions = await Get(VersionList);

        Assert.Equal(HttpStatusCode.Unauthorized, package.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, versions.StatusCode);
        Assert.Contains("Basic", package.Headers.WwwAuthenticate.ToString());

        // ...and with its login the same client is then served from the cache.
        Assert.Equal(HttpStatusCode.OK, (await Get(Nupkg, AliceAuth)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(VersionList, AliceAuth)).StatusCode);
    }

    [Fact]
    public async Task Permission_older_than_the_stale_window_is_not_honoured_during_an_outage()
    {
        await Get(Nupkg, AliceAuth);
        _upstream.Down = true;
        _time.Advance(TimeSpan.FromDays(2));

        Assert.Equal(HttpStatusCode.BadGateway, (await Get(Nupkg, AliceAuth)).StatusCode);
    }

    [Fact]
    public async Task An_outage_on_a_miss_is_a_bad_gateway()
    {
        _upstream.Down = true;

        Assert.Equal(HttpStatusCode.BadGateway, (await Get(Nupkg, AliceAuth)).StatusCode);
    }

    [Fact]
    public async Task Upstream_answers_are_passed_on_when_a_client_can_act_on_them()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Nupkg, BobAuth)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("townsuite.missing/index.json", AliceAuth)).StatusCode);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("townsuite.secret/..%2f..%2findex.json")]
    [InlineData("townsuite.secret/1.0.0/townsuite.secret.1.0.0.nupkg.sha512")]
    [InlineData("not-a-package")]
    public async Task Paths_that_are_not_package_requests_never_reach_the_cache_or_the_upstream(string path)
    {
        var response = await Get(path, AliceAuth);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _upstream.Gets + _upstream.Heads);
    }

    [Fact]
    public async Task Uppercase_requests_share_one_cache_entry()
    {
        await Get("TownSuite.Secret/1.0.0/TownSuite.Secret.1.0.0.nupkg", AliceAuth);
        await Get(Nupkg, AliceAuth);

        Assert.Equal(1, _upstream.Gets);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_missing_file_download_it_once()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Get(Nupkg, AliceAuth)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, _upstream.Gets);
    }

    [Fact]
    public async Task The_service_index_points_clients_at_the_flat_container()
    {
        var index = await _factory.CreateClient().GetStringAsync("/v3/index.json");

        Assert.Contains("PackageBaseAddress/3.0.0", index);
        Assert.Contains("/v3/flatcontainer/", index);
    }

    [Fact]
    public async Task The_service_index_uses_the_public_url_behind_a_tls_terminating_proxy()
    {
        using var configured = _factory.WithWebHostBuilder(b => b.UseSetting("PublicBaseUrl", "https://nuget-cache.example.com/"));

        var index = await configured.CreateClient().GetStringAsync("/v3/index.json");

        Assert.Contains("https://nuget-cache.example.com/v3/flatcontainer/", index);
    }

    [Fact]
    public async Task Public_upstreams_can_skip_the_credential_check()
    {
        using var open = _factory.WithWebHostBuilder(b => b.UseSetting("ValidateCredentials", "false"));
        _upstream.RequiredAuthorization = null;
        var client = open.CreateClient();
        await client.GetAsync("/v3/flatcontainer/" + Nupkg);
        _time.Advance(TimeSpan.FromHours(1));

        var again = await client.GetAsync("/v3/flatcontainer/" + Nupkg);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, _upstream.Heads);
    }
}
