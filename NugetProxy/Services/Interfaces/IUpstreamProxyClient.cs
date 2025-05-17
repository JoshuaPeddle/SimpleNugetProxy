namespace NugetProxy.Services.Interfaces
{
    public interface IUpstreamProxyClient
    {
        Task<HttpResponseMessage> GetAsync(string requestUri, IHeaderDictionary requestHeaders);
    }
}