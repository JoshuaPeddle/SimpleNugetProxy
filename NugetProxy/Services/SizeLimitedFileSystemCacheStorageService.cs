using Microsoft.Extensions.Options;
using NugetProxy.Services.Interfaces;

namespace NugetProxy.Services
{
    /// <summary>
    /// A file system cache that removes the least recently used files when it passes its size limit.
    ///
    /// The index of what is cached lives in memory and is built once at start-up, so eviction never
    /// walks the directory. The lock covers only index updates: downloads are copied to a temporary
    /// file outside it and renamed into place, so a reader never sees a partly written file.
    /// </summary>
    public sealed class SizeLimitedFileSystemCacheStorageService : ICacheStorageService
    {
        private const long Megabyte = 1024L * 1024L;
        private const string TempDirectoryName = ".tmp";

        private readonly string _root;
        private readonly string _tempRoot;
        private readonly long _maxBytes;
        private readonly TimeProvider _time;
        private readonly ILogger<SizeLimitedFileSystemCacheStorageService> _logger;

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
        private long _currentBytes;

        private sealed class Entry
        {
            public long Length;
            public long LastAccessTicks;
        }

        public SizeLimitedFileSystemCacheStorageService(
            IOptions<ProxyOptions> options,
            TimeProvider time,
            ILogger<SizeLimitedFileSystemCacheStorageService> logger)
        {
            _time = time;
            _logger = logger;
            _root = Path.GetFullPath(Path.Combine(options.Value.CacheRoot, "v3"));
            _tempRoot = Path.Combine(_root, TempDirectoryName);
            _maxBytes = (options.Value.MaxCacheSizeMB > 0 ? options.Value.MaxCacheSizeMB : 500) * Megabyte;

            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_tempRoot);
            LoadIndex();
        }

        public long CurrentSizeBytes
        {
            get
            {
                lock (_gate)
                {
                    return _currentBytes;
                }
            }
        }

        // Files already on disk are ordered by when they were last written, the best that survives a restart.
        private void LoadIndex()
        {
            foreach (var leftover in Directory.EnumerateFiles(_tempRoot))
            {
                TryDelete(leftover);
            }

            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.FullName.StartsWith(_tempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                _index[key] = new Entry { Length = file.Length, LastAccessTicks = file.LastWriteTimeUtc.Ticks };
                _currentBytes += file.Length;
            }

            _logger.LogInformation("Cache at {Root} holds {Count} files, {Megabytes} MB of {MaxMegabytes} MB",
                _root, _index.Count, _currentBytes / Megabyte, _maxBytes / Megabyte);
        }

        // Defence in depth: PackagePath has already limited what a request can name.
        private string ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || full.StartsWith(_tempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ArgumentException("Path is outside the cache.", nameof(relativePath));
            }

            return full;
        }

        public Stream? TryOpenRead(string relativePath)
        {
            var path = ResolvePath(relativePath);

            lock (_gate)
            {
                if (!_index.TryGetValue(relativePath, out var entry))
                {
                    return null;
                }

                entry.LastAccessTicks = _time.GetUtcNow().UtcTicks;
            }

            try
            {
                // FileShare.Delete lets an eviction remove the file while a download is still reading it.
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Forget(relativePath);
                return null;
            }
        }

        public async Task<Stream> StoreAsync(string relativePath, Stream content, CancellationToken cancellationToken = default)
        {
            var destination = ResolvePath(relativePath);
            var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
            long length;

            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await content.CopyToAsync(output, cancellationToken);
                    length = output.Length;
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            if (length > _maxBytes)
            {
                _logger.LogWarning("{Path} is {Megabytes} MB, larger than the whole cache; serving it without keeping it", relativePath, length / Megabyte);
                return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }

            List<string> evicted;
            lock (_gate)
            {
                evicted = MakeRoomFor(length, relativePath);
            }

            // Deleting outside the lock keeps slow disks from stalling readers.
            foreach (var key in evicted)
            {
                var path = ResolvePath(key);
                TryDelete(path);
                RemoveEmptyDirectories(Path.GetDirectoryName(path));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(temp, destination, overwrite: true);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another request stored the same file first and has it open. Package files never
                // change once published, so the copy already in place is as good as this one.
                TryDelete(temp);
                length = new FileInfo(destination).Length;
            }

            lock (_gate)
            {
                if (_index.TryGetValue(relativePath, out var existing))
                {
                    _currentBytes -= existing.Length;
                }

                _index[relativePath] = new Entry { Length = length, LastAccessTicks = _time.GetUtcNow().UtcTicks };
                _currentBytes += length;
            }

            return TryOpenRead(relativePath)
                   ?? throw new IOException($"{relativePath} was removed from the cache while it was being stored.");
        }

        // Called with the lock held. Removes entries from the index and returns their keys; the files go afterwards.
        private List<string> MakeRoomFor(long incomingBytes, string incomingKey)
        {
            var evicted = new List<string>();

            var replacing = _index.TryGetValue(incomingKey, out var current) ? current.Length : 0;
            var overflow = _currentBytes - replacing + incomingBytes - _maxBytes;
            if (overflow <= 0)
            {
                return evicted;
            }

            foreach (var pair in _index.Where(p => p.Key != incomingKey).OrderBy(p => p.Value.LastAccessTicks).ToList())
            {
                if (overflow <= 0)
                {
                    break;
                }

                _index.Remove(pair.Key);
                _currentBytes -= pair.Value.Length;
                overflow -= pair.Value.Length;
                evicted.Add(pair.Key);
                _logger.LogInformation("Evicting {Path} ({Kilobytes} KB) to stay within the cache size", pair.Key, pair.Value.Length / 1024);
            }

            return evicted;
        }

        private void Forget(string relativePath)
        {
            lock (_gate)
            {
                if (_index.Remove(relativePath, out var entry))
                {
                    _currentBytes -= entry.Length;
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open by a reader on a platform that forbids deleting it; it is already out of the index.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void RemoveEmptyDirectories(string? directory)
        {
            try
            {
                while (!string.IsNullOrEmpty(directory)
                       && directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                       && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (IOException)
            {
                // A concurrent store created something in it; leave it.
            }
        }
    }
}
