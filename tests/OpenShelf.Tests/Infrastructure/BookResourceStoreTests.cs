using System.Collections.Concurrent;
using OpenShelf.Core;
using OpenShelf.Infrastructure;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class BookResourceStoreTests
{
    [Fact]
    public void ResolvesDecodedNestedBookPathsAndStableIds()
    {
        var store = new BookResourceStore(
        [
            new BookResource(
                "cover-id",
                "image/png",
                [1, 2, 3],
                "OPS/Images/cover art.png"),
            new BookResource(
                "icon-id",
                "image/svg+xml",
                [4, 5],
                "OPS/Text/icons/next.svg")
        ]);

        Assert.True(store.TryResolve(
            "OPS/Text/chapter-1.xhtml",
            "../Images/cover%20art.png?width=400#view",
            out var coverId));
        Assert.Equal("cover-id", coverId);

        Assert.True(store.TryResolve(
            "OPS/Text/chapter-1.xhtml",
            "icons/next.svg",
            out var iconId));
        Assert.Equal("icon-id", iconId);

        Assert.True(store.TryResolve(
            "OPS/Text/chapter-1.xhtml",
            "cover-id",
            out var directId));
        Assert.Equal("cover-id", directId);
    }

    [Theory]
    [InlineData("OPS/Text/chapter.xhtml", "../../../outside.png")]
    [InlineData("../Text/chapter.xhtml", "image.png")]
    [InlineData("OPS/Text/chapter.xhtml", "/OPS/Images/cover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "%2FOPS%2FImages%2Fcover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "C:%5Cbooks%5Ccover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "https://example.test/cover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "https%3A%2F%2Fexample.test%2Fcover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "//example.test/cover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "data:image/png;base64,AA")]
    [InlineData("OPS/Text/chapter.xhtml", "..%2F..%2F..%2Foutside.png")]
    [InlineData("OPS/Text/chapter.xhtml", "images%5Ccover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "images/%ZZ.png")]
    [InlineData("OPS/Text/chapter.xhtml", "#fragment-only")]
    public void RejectsTraversalAbsoluteRemoteAndMalformedReferences(
        string basePath,
        string reference)
    {
        var store = new BookResourceStore(
        [
            new BookResource(
                "cover-id",
                "image/png",
                [1],
                "OPS/Images/cover.png")
        ]);

        Assert.False(store.TryResolve(basePath, reference, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void DeduplicatesContentAndIsolatesItFromSourceMutation()
    {
        byte[] firstSource = [10, 20, 30, 40];
        byte[] secondSource = [10, 20, 30, 40];
        var store = new BookResourceStore(
        [
            new BookResource("first", "image/png", firstSource, "images/first.png"),
            new BookResource("second", "image/png", secondSource, "images/second.png")
        ]);
        firstSource[0] = 99;
        secondSource[1] = 99;

        Assert.Equal(2, store.ResourceCount);
        Assert.Equal(1, store.UniquePayloadCount);
        Assert.True(store.TryGet("first", out var first));
        Assert.True(store.TryGet("second", out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, first.Data);
        Assert.NotSame(first.Data, second.Data);
        first.Data[0] = 77;
        Assert.True(store.TryGet("first", out var firstAgain));
        Assert.True(store.TryGet("second", out var secondAgain));
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, firstAgain!.Data);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, secondAgain!.Data);
        Assert.True(store.TryGetContentHash("first", out var firstHash));
        Assert.True(store.TryGetContentHash("second", out var secondHash));
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(64, firstHash.Length);
    }

    [Fact]
    public void RejectsConflictingStableIdsAndPathAliases()
    {
        Assert.Throws<ArgumentException>(() => new BookResourceStore(
        [
            new BookResource("same-id", "image/png", [1], "images/one.png"),
            new BookResource("same-id", "image/png", [2], "images/two.png")
        ]));

        Assert.Throws<ArgumentException>(() => new BookResourceStore(
        [
            new BookResource("one", "image/png", [1], "images/shared.png"),
            new BookResource("two", "image/png", [2], "images/shared.png")
        ]));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedPayloadWithinEntryAndByteBounds()
    {
        var store = new BookResourceStore(
        [
            new BookResource("a", "application/octet-stream", [1, 1, 1, 1], "a.bin"),
            new BookResource("b", "application/octet-stream", [2, 2, 2, 2], "b.bin"),
            new BookResource("c", "application/octet-stream", [3, 3, 3, 3], "c.bin"),
            new BookResource(
                "oversized",
                "application/octet-stream",
                [4, 4, 4, 4, 4, 4, 4, 4, 4],
                "oversized.bin")
        ],
        new ResourceCacheOptions(MaximumEntries: 2, MaximumBytes: 8));

        Assert.True(store.TryGet("a", out _));
        Assert.True(store.TryGet("b", out _));
        Assert.True(store.TryGet("a", out _));
        Assert.True(store.TryGet("c", out _));

        Assert.True(store.IsCached("a"));
        Assert.False(store.IsCached("b"));
        Assert.True(store.IsCached("c"));
        Assert.True(store.TryGet("oversized", out _));
        Assert.False(store.IsCached("oversized"));
        Assert.Equal(
            new ResourceCacheSnapshot(2, 8, 2, 8),
            store.CacheSnapshot);

        store.ClearCache();
        Assert.Equal(0, store.CacheSnapshot.CachedPayloads);
        Assert.Equal(0, store.CacheSnapshot.CachedBytes);
    }

    [Fact]
    public void ConcurrentResolutionAndReadsRemainWithinCacheBounds()
    {
        var resources = Enumerable.Range(0, 12)
            .Select(index => new BookResource(
                $"resource-{index}",
                "application/octet-stream",
                [(byte)index, (byte)(index + 1)],
                $"assets/{index}.bin"))
            .ToArray();
        var store = new BookResourceStore(
            resources,
            new ResourceCacheOptions(MaximumEntries: 3, MaximumBytes: 6));
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.For(
            0,
            2_000,
            iteration =>
            {
                try
                {
                    var index = iteration % resources.Length;
                    Assert.True(store.TryResolve(
                        string.Empty,
                        $"assets/{index}.bin",
                        out var id));
                    Assert.True(store.TryGet(id, out var resource));
                    Assert.Equal((byte)index, resource!.Data[0]);
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
            });

        Assert.Empty(exceptions);
        Assert.InRange(store.CacheSnapshot.CachedPayloads, 0, 3);
        Assert.InRange(store.CacheSnapshot.CachedBytes, 0, 6);
    }
}
