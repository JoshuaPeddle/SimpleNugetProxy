# SimpleNugetProxy

A small caching proxy for a NuGet v3 feed. Point your builds at it and each package file is downloaded from the
upstream feed once, then served from local disk. It is built for CI: many build machines restoring the same
packages, some of them private.

It serves the NuGet *flat container* only (version lists, `.nupkg` and `.nuspec` files), which is all
`dotnet restore` needs. It is not a package host and does not search or push.

## How it behaves

| Request | What happens |
|---|---|
| `.nupkg`, `.nuspec` | Package files never change once published, so they are cached until evicted. |
| `{id}/index.json` (version list) | **Always asked from the upstream**, so a newly published version is visible at once. The cached copy is used only when the upstream is down. |
| Anything else | `404`. Paths are checked against the shapes NuGet itself asks for before they get near the file system. |

### Private feeds

The cache is shared by everyone who can reach the proxy, but the upstream's permissions are per caller. When a
file is already cached, the proxy asks the upstream (a `HEAD` with the caller's own `Authorization` header)
whether that caller may have it, and serves it only if the answer is yes. A cached private package is therefore
never handed to someone the upstream would refuse.

- The answer "allowed" is reused for the same credentials and package id for `AuthCacheSeconds` (5 minutes), so
  a busy build does not ask once per file. A refusal is never remembered.
- If the upstream is unreachable, a caller it allowed within `AuthStaleSeconds` (a day) is still served from the
  cache, so an upstream outage does not stop builds that only need packages already cached. A caller that sent
  no login gets `401`, so NuGet then sends its credentials; anyone else gets `502`.
- For a public upstream such as nuget.org there is nothing to protect: set `ValidateCredentials` to `false`.

Credentials are the client's own. Put the upstream feed's login in the NuGet config on the build machine and the
proxy passes it on; the proxy stores no credentials. It answers `401` with a `WWW-Authenticate` challenge so
NuGet sends them.

### The cache

Files are kept on disk under `CacheRoot`. When the total passes `MaxCacheSizeMB`, the least recently used files
are removed. A package is downloaded once even when many builds ask for it at the same moment, files are written
to a temporary name and renamed into place, and a file larger than the whole cache is served but not kept.

## Configuration

Settings are in `appsettings.json`, and any of them can be set by an environment variable of the same name.

| Setting | Default | Meaning |
|---|---|---|
| `UpstreamBase` | `https://api.nuget.org/v3/flatcontainer/` | The upstream feed's flat container URL. One upstream per proxy: run one proxy per feed. |
| `CacheRoot` | `nuget-cache` | Where package files are kept. |
| `MaxCacheSizeMB` | `500` | Size limit before least-recently-used files are removed. |
| `ValidateCredentials` | `true` in code, `false` in the shipped `appsettings.json` | Ask the upstream before serving a cached file. Turn on for any private upstream. |
| `AuthCacheSeconds` | `300` | How long an "allowed" answer is reused. |
| `AuthStaleSeconds` | `86400` | How long a recent "allowed" is trusted while the upstream is down. |
| `PublicBaseUrl` | request's own address | The address clients use, e.g. `https://nuget-cache.example.com`. Set it behind a reverse proxy that terminates TLS. |

The proxy listens on HTTP only (`http://0.0.0.0:5182`, set under `Kestrel` in `appsettings.json`). Put a reverse
proxy in front for TLS, and set `PublicBaseUrl`.

Example for a private feed on a ProGet server:

```sh
docker run -d --name nuget-cache -p 5182:5182 -v nuget-cache:/cache \
  -e UpstreamBase=https://proget.example.com/nuget/Internal/v3/flatcontainer/ \
  -e ValidateCredentials=true \
  -e PublicBaseUrl=https://nuget-cache.example.com \
  nugetproxy
```

## Client configuration

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="cache" value="https://nuget-cache.example.com/v3/index.json" />
  </packageSources>
  <!-- For a private upstream: the upstream feed's own login. -->
  <packageSourceCredentials>
    <cache>
      <add key="Username" value="ci-user" />
      <add key="ClearTextPassword" value="..." />
    </cache>
  </packageSourceCredentials>
</configuration>
```

An `http://` source needs `allowInsecureConnections="true"` on the `<add>`.

## Run

Docker (a chiseled, non-root image with no shell; the cache is the `/cache` volume):

```sh
docker build -f NugetProxy/Dockerfile -t nugetproxy .
docker run -d -p 5182:5182 -v nuget-cache:/cache nugetproxy
```

A bind mount instead of a named volume must be writable by uid 1654, the image's `app` user.

From source: `dotnet run --project NugetProxy`. Tests: `dotnet test NugetProxy.Tests`.

`GET /health` returns `OK`.

## Endpoints

* `GET /v3/index.json`: the service index.
* `GET /v3/flatcontainer/{id}/index.json`: version list.
* `GET /v3/flatcontainer/{id}/{version}/{id}.{version}.nupkg` and `{id}/{version}/{id}.nuspec`: package files.
* `GET /health`

## Limits

* One upstream per proxy instance.
* No vulnerability data: the service index offers only the flat container. `NuGetAudit` has nothing to check
  against this source, so add nuget.org as an `<auditSources>` entry if you rely on it.
* The "allowed" answers are held in memory only: after a proxy restart during an upstream outage, clients that
  send a login are refused until the upstream is back.

## Test project

`NugetProxy.PackageHolder` is a project with many package references, used to exercise the proxy by hand:
`NugetProxy.PackageHolder/test.ps1` clears the local and proxy caches and restores it through the proxy.

## Licence

MIT. See [LICENSE](LICENSE).
