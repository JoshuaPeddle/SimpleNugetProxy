using NugetProxy.Services.Interfaces;

namespace NugetProxy.Services
{
    public class FileSystemCacheStorageService : ICacheStorageService
    {
        private readonly string _cacheRoot;

        public FileSystemCacheStorageService(IConfiguration configuration)
        {
            _cacheRoot = Path.Combine(configuration["CacheRoot"] ?? "nuget-cache", "v3");
            if (!Directory.Exists(_cacheRoot))
            {
                Directory.CreateDirectory(_cacheRoot);
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public Stream OpenReadStream(string path) => File.OpenRead(path);

        public async Task SaveFileAsync(string path, Stream contentStream)
        {
            EnsureDirectoryExists(Path.GetDirectoryName(path)!);
            await using var fileStream = File.Create(path);
            await contentStream.CopyToAsync(fileStream);
        }

        public void EnsureDirectoryExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public string GetFullPath(string relativePath) => Path.Combine(_cacheRoot, relativePath.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));
    }
}