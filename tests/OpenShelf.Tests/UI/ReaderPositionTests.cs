using System.Collections.Immutable;
using OpenShelf.App.Services;
using OpenShelf.App.ViewModels;
using OpenShelf.Core;
using Xunit;
using CoreAnnotationKind = OpenShelf.Core.AnnotationKind;
using UiAnnotationKind = OpenShelf.App.ViewModels.AnnotationKind;

namespace OpenShelf.Tests.UI;

public sealed class ReaderPositionTests
{
    [Theory]
    [InlineData("page:5@0.4000", 5, 0.4)]
    [InlineData("page:0@1", 0, 1)]
    [InlineData("page:12", 12, 0)]
    public void PdfLocatorRoundTripsPageAndWithinPage(
        string locator,
        int expectedPage,
        double expectedWithinPage)
    {
        Assert.True(
            ReaderPositionLocator.TryParsePdf(
                locator,
                out var pageIndex,
                out var withinPage));
        Assert.Equal(expectedPage, pageIndex);
        Assert.Equal(expectedWithinPage, withinPage, 4);
        Assert.True(
            ReaderPositionLocator.TryParsePdf(
                ReaderPositionLocator.FormatPdf(pageIndex, withinPage),
                out var roundTrippedPage,
                out var roundTrippedWithinPage));
        Assert.Equal(pageIndex, roundTrippedPage);
        Assert.Equal(withinPage, roundTrippedWithinPage, 4);
    }

    [Theory]
    [InlineData("page:five@0.4")]
    [InlineData("page:-1@0.4")]
    [InlineData("page:5@invalid")]
    public void InvalidPdfLocatorIsRejected(string locator)
    {
        Assert.False(ReaderPositionLocator.TryParsePdf(locator, out _, out _));
    }

    [Fact]
    public void PdfBookmarkMappingPreservesWithinPagePosition()
    {
        var bookId = Guid.NewGuid();
        var annotation = new AnnotationItemViewModel(
            Guid.NewGuid(),
            UiAnnotationKind.Bookmark,
            "page:5@0.4000",
            "Middle of page six",
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);

        var anchor = Assert.IsType<PdfAnchor>(
            ProductionLibraryGateway.CreateAnchor(
                bookId,
                "revision",
                document: null,
                annotation));
        var stored = new Annotation(
            annotation.Id,
            bookId,
            CoreAnnotationKind.Bookmark,
            anchor,
            annotation.CreatedAt,
            annotation.CreatedAt,
            Label: annotation.Title);
        var restored = ProductionLibraryGateway.MapAnnotation(stored);

        Assert.Equal(5, anchor.PageIndex);
        Assert.Equal(0.4, anchor.WithinPageOffset, 4);
        Assert.Equal("page:5@0.4000", restored.Locator);
    }

    [Fact]
    public void ReflowableReadingAnchorPreservesCharacterOffset()
    {
        var bookId = Guid.NewGuid();
        var anchor = Assert.IsType<ReflowableAnchor>(
            ProductionLibraryGateway.CreatePositionAnchor(
                bookId,
                document: null,
                "chapter-2|paragraph-4#137",
                "revision"));
        var position = new ReadingPosition(
            bookId,
            anchor,
            0.5,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.Equal(137, anchor.StartOffset);
        Assert.Equal(137, anchor.EndOffset);
        Assert.Equal(
            "chapter-2|paragraph-4#137",
            ProductionLibraryGateway.MapReadingLocator(position));
    }

    [Fact]
    public void DocumentCacheIsBoundedAndUsesLeastRecentlyUsedEviction()
    {
        var cache = new BoundedBookDocumentCache(capacity: 2);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var first = CreateDocument("first");
        var second = CreateDocument("second");
        var third = CreateDocument("third");

        cache.Set(firstId, first);
        cache.Set(secondId, second);
        Assert.True(cache.TryGet(firstId, out _));
        cache.Set(thirdId, third);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(firstId, out var cachedFirst));
        Assert.Same(first, cachedFirst);
        Assert.False(cache.TryGet(secondId, out _));
        Assert.True(cache.TryGet(thirdId, out var cachedThird));
        Assert.Same(third, cachedThird);
    }

    private static ReflowableDocument CreateDocument(string revision)
    {
        return new ReflowableDocument(
            new BookMetadata("Title", "Author"),
            revision,
            0,
            ImmutableArray<DocumentSection>.Empty,
            ImmutableArray<TableOfContentsItem>.Empty,
            ImmutableDictionary<string, BookResource>.Empty);
    }
}
