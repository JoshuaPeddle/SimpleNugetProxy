using System.Net.Http.Headers;

namespace NugetProxy.Services
{
    public class HttpUpstreamProxyClient : IUpstreamProxyClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private const string HttpClientName = "UpstreamBase";

        public HttpUpstreamProxyClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<HttpResponseMessage> GetAsync(string requestUri, IHeaderDictionary requestHeaders)
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);

            if (requestHeaders.TryGetValue("Authorization", out var authHeaderValues))
            {
                string? authHeaderValue = authHeaderValues.FirstOrDefault();
                if (authHeaderValue != null && AuthenticationHeaderValue.TryParse(authHeaderValue, out var parsedAuthHeader))
                {
                    client.DefaultRequestHeaders.Authorization = parsedAuthHeader;
                }
            }
            
            return await client.GetAsync(requestUri);
        }
    }
}