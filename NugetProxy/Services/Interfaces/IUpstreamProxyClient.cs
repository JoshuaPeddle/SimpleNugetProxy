using System.Net;

namespace NugetProxy.Services.Interfaces
{
    public interface IUpstreamProxyClient
    {
        /// <summary>
        /// GETs a path relative to the upstream flat container. The caller disposes the response.
        /// Throws HttpRequestException (or TaskCanceledException on timeout) when the upstream cannot be reached.
        /// </summary>
        Task<HttpResponseMessage> GetAsync(string relativePath, string? authorization, CancellationToken cancellationToken);

        /// <summary>
        /// Asks the upstream whether the caller may read a path, without downloading it: HEAD, or a GET
        /// that stops after the headers for an upstream that does not answer HEAD.
        /// </summary>
        Task<HttpStatusCode> HeadAsync(string relativePath, string? authorization, CancellationToken cancellationToken);
    }
}
