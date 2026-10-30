using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NugetProxy.Services;

namespace NugetProxy.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nugetproxy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SizeLimitedFileSystemCacheStorageService Create(long maxMegabytes = 1)
        => new(Options.Create(new ProxyOptions { CacheRoot = _dir, MaxCacheSizeMB = maxMegabytes }), _time, NullLogger<SizeLimitedFileSystemCacheStorageService>.Instance);

    private static MemoryStream Bytes(int length, byte fill = 1) => new(Enumerable.Repeat(fill, length).ToArray());

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Fact]
    public async Task Stored_content_can_be_read_back()
    {
        var storage = Create();

        await (await storage.StoreAsync("pkg/1.0.0/pkg.1.0.0.nupkg", new MemoryStream(Encoding.UTF8.GetBytes("hello")))).DisposeAsync();

        Assert.Equal("hello", Encoding.UTF8.GetString(await ReadAll(storage.TryOpenRead("pkg/1.0.0/pkg.1.0.0.nupkg")!)));
        Assert.Equal(5, storage.CurrentSizeBytes);
    }

    [Fact]
    public void Unknown_files_are_a_miss()
    {
        Assert.Null(Create().TryOpenRead("pkg/1.0.0/pkg.1.0.0.nupkg"));
    }

    [Fact]
    public async Task Storing_again_replaces_the_file_and_the_count()
    {
        var storage = Create();
        await (await storage.StoreAsync("pkg/index.json", Bytes(100))).DisposeAsync();
        await (await storage.StoreAsync("pkg/index.json", Bytes(40, 2))).DisposeAsync();

        Assert.Equal(40, storage.CurrentSizeBytes);
        Assert.All(await ReadAll(storage.TryOpenRead("pkg/index.json")!), b => Assert.Equal(2, b));
    }

    [Fact]
    public async Task The_least_recently_used_file_goes_first_when_the_cache_is_full()
    {
        var storage = Create(maxMegabytes: 1);
        const int third = 400 * 1024;

        await (await storage.StoreAsync("a/1/a.1.nupkg", Bytes(third))).DisposeAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await (await storage.StoreAsync("b/1/b.1.nupkg", Bytes(third))).DisposeAsync();
        _time.Advance(TimeSpan.FromMinutes(1));

        // Reading "a" makes "b" the oldest.
        (await ReadAll(storage.TryOpenRead("a/1/a.1.nupkg")!)).GetHashCode();
        _time.Advance(TimeSpan.FromMinutes(1));

        await (await storage.StoreAsync("c/1/c.1.nupkg", Bytes(third))).DisposeAsync();

        Assert.NotNull(storage.TryOpenRead("a/1/a.1.nupkg"));
        Assert.Null(storage.TryOpenRead("b/1/b.1.nupkg"));
        Assert.NotNull(storage.TryOpenRead("c/1/c.1.nupkg"));
        Assert.True(storage.CurrentSizeBytes <= 1024 * 1024);
        Assert.False(Directory.Exists(Path.Combine(_dir, "v3", "b")), "the evicted package's empty directory should be removed");
    }

    [Fact]
    public async Task A_file_larger_than_the_whole_cache_is_served_but_not_kept()
    {
        var storage = Create(maxMegabytes: 1);

        var stream = await storage.StoreAsync("big/1/big.1.nupkg", Bytes(2 * 1024 * 1024));
        Assert.Equal(2 * 1024 * 1024, (await ReadAll(stream)).Length);

        Assert.Null(storage.TryOpenRead("big/1/big.1.nupkg"));
        Assert.Equal(0, storage.CurrentSizeBytes);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "v3", ".tmp")));
    }

    [Fact]
    public async Task Eviction_does_not_break_a_download_already_in_progress()
    {
        var storage = Create(maxMegabytes: 1);
        const int half = 600 * 1024;
        await (await storage.StoreAsync("a/1/a.1.nupkg", Bytes(half))).DisposeAsync();

        await using var reading = storage.TryOpenRead("a/1/a.1.nupkg")!;
        _time.Advance(TimeSpan.FromMinutes(1));
        await (await storage.StoreAsync("b/1/b.1.nupkg", Bytes(half))).DisposeAsync();

        Assert.Null(storage.TryOpenRead("a/1/a.1.nupkg"));
        using var copy = new MemoryStream();
        await reading.CopyToAsync(copy);
        Assert.Equal(half, copy.Length);
    }

    [Fact]
    public async Task The_index_is_rebuilt_from_disk_and_leftover_temp_files_are_cleaned_up()
    {
        var first = Create();
        await (await first.StoreAsync("pkg/1.0.0/pkg.1.0.0.nupkg", Bytes(123))).DisposeAsync();
        File.WriteAllText(Path.Combine(_dir, "v3", ".tmp", "interrupted"), "partial");

        var second = Create();

        Assert.Equal(123, second.CurrentSizeBytes);
        Assert.NotNull(second.TryOpenRead("pkg/1.0.0/pkg.1.0.0.nupkg"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "v3", ".tmp")));
    }

    [Fact]
    public async Task A_file_deleted_behind_its_back_becomes_a_miss()
    {
        var storage = Create();
        await (await storage.StoreAsync("pkg/index.json", Bytes(10))).DisposeAsync();
        File.Delete(Path.Combine(_dir, "v3", "pkg", "index.json"));

        Assert.Null(storage.TryOpenRead("pkg/index.json"));
        Assert.Equal(0, storage.CurrentSizeBytes);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("pkg/../../outside.txt")]
    [InlineData(".tmp/stolen")]
    public async Task Paths_outside_the_cache_are_refused(string path)
    {
        var storage = Create();

        Assert.Throws<ArgumentException>(() => storage.TryOpenRead(path));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StoreAsync(path, Bytes(1)));
    }

    [Fact]
    public async Task Many_concurrent_stores_keep_the_size_accurate()
    {
        var storage = Create(maxMegabytes: 4);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
            await (await storage.StoreAsync($"p{i}/1/p{i}.1.nupkg", Bytes(100 * 1024))).DisposeAsync()));

        var onDisk = new DirectoryInfo(Path.Combine(_dir, "v3")).GetFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Directory!.Name != ".tmp").Sum(f => f.Length);
        Assert.Equal(onDisk, storage.CurrentSizeBytes);
        Assert.True(onDisk <= 4 * 1024 * 1024);
    }
}
