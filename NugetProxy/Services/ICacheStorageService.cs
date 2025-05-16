namespace NugetProxy.Services
{
    public interface ICacheStorageService
    {
        bool FileExists(string path);
        Stream OpenReadStream(string path);
        Task SaveFileAsync(string path, Stream contentStream);
        void EnsureDirectoryExists(string path);
        string GetFullPath(string relativePath);
    }
}