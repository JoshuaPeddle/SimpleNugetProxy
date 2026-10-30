namespace NugetProxy.Services.Interfaces
{
    public interface ICacheStorageService
    {
        /// <summary>Opens a cached file for reading, or returns null if it is not cached.</summary>
        Stream? TryOpenRead(string relativePath);

        /// <summary>
        /// Stores the content under the relative path and returns a stream over the stored copy.
        /// A file larger than the whole cache is not kept: the returned stream then reads a
        /// temporary file that is deleted when the stream is disposed.
        /// </summary>
        Task<Stream> StoreAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);

        /// <summary>Total bytes currently cached.</summary>
        long CurrentSizeBytes { get; }
    }
}
