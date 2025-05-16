namespace NugetProxy.Services
{
    public interface IUpstreamProxyClient
    {
        Task<HttpResponseMessage> GetAsync(string requestUri, IHeaderDictionary requestHeaders);
    }
}