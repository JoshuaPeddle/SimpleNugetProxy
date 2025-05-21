# SimpleNugetProxy

## Description

This project implements a simple NuGet v3 proxy server. It forwards requests to an upstream NuGet feed and caches package contents locally to reduce redundant downloads and improve access speed for subsequent requests.

## Features

*   **NuGet v3 Proxy**: Acts as a local proxy for a configurable upstream NuGet v3 feed.
*   **Package Caching**: Downloads and caches NuGet packages to a local directory.
*   **Configurable Upstream Feed**: The URL for the upstream NuGet feed can be specified in the configuration.
*   **Configurable Cache Location**: The directory for storing cached packages can be customized.
*   **Authentication Forwarding**: Supports forwarding of `Authorization` headers to the upstream feed, allowing access to private or authenticated feeds.
*   **Standard NuGet Endpoints**:
    *   Service Index: `/v3/index.json`
    *   Package Content (Flat Container): `/v3/flatcontainer/`
*   **Health Check**: Includes a `/health` endpoint that returns "OK" for basic service availability monitoring.

## Configuration

The application is configured using the `appsettings.json` file.

Key configuration settings:

*   `UpstreamBase`: The base URL of the upstream NuGet v3 flat container.
    *   Example: `"https://api.nuget.org/v3/flatcontainer/"`
*   `CacheRoot`: The path to the directory where NuGet packages will be cached. This can be an absolute or relative path.
    *   Example: `"nuget-cache"`
*   `MaxCacheSizeMB`: The maximum diskspace occupied by the cache.
    *   Example: `500`
 
## Usage

1.  **Configure the Application**:
    *   Modify the `appsettings.json` file to set your desired `UpstreamBase` URL and `CacheRoot` directory.

2.  **Run the Application**:
    *   Build and run the ASP.NET Core project. By default, it might run on a port like 5000 or 7000 (e.g., `http://localhost:5000`). Check the application's console output for the exact URL.

3.  **Configure NuGet Client**:
    *   Update your NuGet client (e.g., Visual Studio Package Manager settings, `nuget.config` file) to use the proxy's service index URL.
    *   The service index URL will be `http://<host>:<port>/v3/index.json` (replace `<host>` and `<port>` with the actual host and port where the proxy is running).

    Example `nuget.config` entry:
    ```xml
    <?xml version="1.0" encoding="utf-8"?>
    <configuration>
      <packageSources>
        <clear />
        <add key="LocalNugetProxy" value="http://localhost:5000/v3/index.json" />
        <!-- Add other sources if needed, or use only the proxy -->
      </packageSources>
    </configuration>
    ```

Once configured, your NuGet client will route package requests through this proxy. Packages will be fetched from the upstream source on the first request and served from the local cache for subsequent requests.

## Endpoints

*   `GET /v3/index.json`: The NuGet service index.
*   `GET /v3/flatcontainer/{package-id}/{version}/{package-id}.{version}.nupkg`: Endpoint for downloading NuGet package files.
*   `GET /v3/flatcontainer/{package-id}/index.json`: Endpoint for listing available versions of a package.
*   `GET /health`: Health check endpoint. Returns `OK` with a 200 status code if the service is running.
