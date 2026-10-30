using System.Net;
using System.Net.Http.Headers;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Services
{
    public class HttpUpstreamProxyClient : IUpstreamProxyClient
    {
        private readonly HttpClient _client;

        public HttpUpstreamProxyClient(HttpClient client)
        {
            _client = client;
        }

        public Task<HttpResponseMessage> GetAsync(string relativePath, string? authorization, CancellationToken cancellationToken)
            => SendAsync(HttpMethod.Get, relativePath, authorization, cancellationToken);

        public async Task<HttpStatusCode> HeadAsync(string relativePath, string? authorization, CancellationToken cancellationToken)
        {
            using var head = await SendAsync(HttpMethod.Head, relativePath, authorization, cancellationToken);
            if (head.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                return head.StatusCode;
            }

            // Headers only: the body is never read, and disposing the response drops the connection.
            using var get = await SendAsync(HttpMethod.Get, relativePath, authorization, cancellationToken);
            return get.StatusCode;
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, string? authorization, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(method, relativePath);

            // Only a well-formed Authorization header is passed on, so the caller cannot smuggle anything else in.
            if (authorization != null && AuthenticationHeaderValue.TryParse(authorization, out var parsed))
            {
                request.Headers.Authorization = parsed;
            }

            return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }
}
