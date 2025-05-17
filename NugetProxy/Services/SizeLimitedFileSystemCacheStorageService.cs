using Microsoft.Extensions.Configuration;
using NugetProxy.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NugetProxy.Services
{
    public class SizeLimitedFileSystemCacheStorageService : ICacheStorageService
    {
        private readonly string _cacheRoot;
        private readonly string _normalizedCacheRoot;
        private readonly long _maxCacheSizeInBytes;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private const long Megabyte = 1024L * 1024L;
        private const long DefaultMaxCacheSizeMB = 1024; // 1 GB in MB

        public SizeLimitedFileSystemCacheStorageService(IConfiguration configuration)
        {
            _cacheRoot = Path.Combine(configuration["CacheRoot"] ?? "nuget-cache", "v3");
            _normalizedCacheRoot = Path.GetFullPath(_cacheRoot);    

            if (!Directory.Exists(_cacheRoot))
            {
                Directory.CreateDirectory(_cacheRoot);
            }

            string maxCacheSizeConfig = configuration["MaxCacheSizeMB"];
            if (long.TryParse(maxCacheSizeConfig, out long maxCacheSizeMB) && maxCacheSizeMB > 0)
            {
                _maxCacheSizeInBytes = maxCacheSizeMB * Megabyte;
            }
            else
            {
                _maxCacheSizeInBytes = DefaultMaxCacheSizeMB * Megabyte;
                Console.WriteLine($"MaxCacheSizeMB not configured or invalid, using default: {DefaultMaxCacheSizeMB} MB ({_maxCacheSizeInBytes} bytes).");
            }
        }

        public string GetFullPath(string relativePath) => Path.Combine(_cacheRoot, relativePath.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));

        public void EnsureDirectoryExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public Stream OpenReadStream(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("File not found.", path);
            }

            _semaphore.Wait();
            try
            {
                if (!File.Exists(path)) 
                {
                     throw new FileNotFoundException("File not found (disappeared before access time update).", path);
                }
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            finally
            {
                _semaphore.Release();
            }
            
            return File.OpenRead(path);
        }

        public async Task SaveFileAsync(string path, Stream contentStream)
        {
            EnsureDirectoryExists(Path.GetDirectoryName(path)!);

            string tempFilePath = Path.GetTempFileName();
            long newFileLength;

            await _semaphore.WaitAsync();
            try
            {
                await using (var tempFileStream = File.Create(tempFilePath))
                {
                    await contentStream.CopyToAsync(tempFileStream);
                }
                newFileLength = new FileInfo(tempFilePath).Length;

                await EnsureCapacityAsync(newFileLength);

                File.Move(tempFilePath, path, overwrite: true);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow); 
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
                _semaphore.Release();
            }
        }

        private async Task EnsureCapacityAsync(long sizeOfFileToBeAdded)
        {

            if (sizeOfFileToBeAdded > _maxCacheSizeInBytes)
            {
                throw new IOException($"File size ({sizeOfFileToBeAdded} bytes) exceeds the total maximum cache size ({_maxCacheSizeInBytes} bytes).");
            }

            var cacheFiles = GetCacheFilesSortedByAccessTime().ToList();
            long currentCacheSize = cacheFiles.Sum(f => f.Length);

            long spaceToMake = (currentCacheSize + sizeOfFileToBeAdded) - _maxCacheSizeInBytes;

            if (spaceToMake <= 0)
            {
                return; 
            }

            foreach (var fileToEvict in cacheFiles)
            {
                if (spaceToMake <= 0) break;

                try
                {
                    string directoryOfEvictedFile = Path.GetDirectoryName(fileToEvict.FullName);
                    long evictedFileSize = fileToEvict.Length;
                    fileToEvict.Delete();
                    Console.WriteLine($"Evicted file {fileToEvict.FullName} to free up {evictedFileSize} bytes.");
                    spaceToMake -= evictedFileSize;
                    
                    CleanUpEmptyDirectories(directoryOfEvictedFile);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Failed to evict file {fileToEvict.FullName}: {ex.Message}");
                }
            }

            currentCacheSize = GetCacheFilesSortedByAccessTime().Sum(f => f.Length);
            if (currentCacheSize + sizeOfFileToBeAdded > _maxCacheSizeInBytes)
            {
                throw new IOException("Could not free enough space in the cache to save the new file after eviction attempts.");
            }
        }

        private IEnumerable<FileInfo> GetCacheFilesSortedByAccessTime()
        {
            var directoryInfo = new DirectoryInfo(_cacheRoot);
            if (!directoryInfo.Exists)
            {
                return Enumerable.Empty<FileInfo>();
            }
            return directoryInfo.GetFiles("*.*", SearchOption.AllDirectories)
                                .OrderBy(f => f.LastAccessTimeUtc);
        }

        private void CleanUpEmptyDirectories(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return;

            string normalizedDirPath = Path.GetFullPath(directoryPath);

            if (!normalizedDirPath.StartsWith(_normalizedCacheRoot, StringComparison.OrdinalIgnoreCase) || 
                string.Equals(normalizedDirPath, _normalizedCacheRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            
            try
            {
                while (Directory.Exists(normalizedDirPath) && !Directory.EnumerateFileSystemEntries(normalizedDirPath).Any())
                {
                    if (string.Equals(normalizedDirPath, _normalizedCacheRoot, StringComparison.OrdinalIgnoreCase)) break;

                    Directory.Delete(normalizedDirPath);
                    Console.WriteLine($"Cleaned up empty directory {normalizedDirPath}");
                    
                    normalizedDirPath = Path.GetDirectoryName(normalizedDirPath);
                    if (string.IsNullOrEmpty(normalizedDirPath) || string.Equals(normalizedDirPath, _normalizedCacheRoot, StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Error cleaning up directory {directoryPath}: {ex.Message}");
            }
        }
    }
}