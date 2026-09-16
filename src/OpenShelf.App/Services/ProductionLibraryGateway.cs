using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenShelf.Core;
using OpenShelf.Formats;
using OpenShelf.Infrastructure;
using OpenShelf.App.ViewModels;
using CoreAnnotationKind = OpenShelf.Core.AnnotationKind;
using CoreImportBatchResult = OpenShelf.Core.ImportBatchResult;
using UiAnnotationKind = OpenShelf.App.ViewModels.AnnotationKind;
using UiImportBatchResult = OpenShelf.App.ViewModels.ImportBatchResult;

namespace OpenShelf.App.Services;

internal sealed class ProductionLibraryGateway : ILibraryGateway, IReaderGateway
{
    private readonly ILibraryRepository _repository;
    private readonly IBookImportService _importer;
    private readonly IBookDocumentService _documents;
    private readonly PdfPageRenderer _pdfRenderer;
    private readonly BoundedBookDocumentCache _openDocuments = new();
    private readonly SemaphoreSlim _positionWriteLock = new(1, 1);

    public ProductionLibraryGateway(
        ILibraryRepository repository,
        IBookImportService importer,
        IBookDocumentService documents,
        PdfPageRenderer pdfRenderer)
    {
        _repository = repository;
        _importer = importer;
        _documents = documents;
        _pdfRenderer = pdfRenderer;
    }

    public async Task<IReadOnlyList<BookDescriptor>> LoadLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        var books = await _repository
            .QueryBooksAsync(new LibraryQuery(), cancellationToken)
            .ConfigureAwait(false);
        return books.Select(MapBook).ToArray();
    }

    public async Task<IReadOnlyList<string>> LoadCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var categories = await _repository
            .GetCategoriesAsync(cancellationToken)
            .ConfigureAwait(false);
        return categories
            .Select(category => category.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<UiImportBatchResult> ImportAsync(
        IReadOnlyList<string> selectedPaths,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var files = selectedPaths.Where(File.Exists).ToArray();
        var folders = selectedPaths.Where(Directory.Exists).ToArray();
        var allItems = new List<ImportItemResult>();

        if (files.Length > 0)
        {
            var result = await _importer
                .ImportFilesAsync(
                    files,
                    CreateImportProgress(progress),
                    cancellationToken)
                .ConfigureAwait(false);
            allItems.AddRange(result.Items);
        }

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _importer
                .ImportFolderAsync(
                    folder,
                    CreateImportProgress(progress),
                    cancellationToken)
                .ConfigureAwait(false);
            allItems.AddRange(result.Items);
        }

        var importedBooks = new List<BookDescriptor>();
        foreach (var bookId in allItems
                     .Where(item =>
                         item.Outcome == ImportOutcome.Imported && item.BookId.HasValue)
                     .Select(item => item.BookId!.Value)
                     .Distinct())
        {
            var book = await _repository
                .GetBookAsync(bookId, cancellationToken)
                .ConfigureAwait(false);
            if (book is not null)
            {
                importedBooks.Add(MapBook(book));
            }
        }

        var results = allItems
            .Select(
                item => new ImportResultItemViewModel(
                    item.DisplayName,
                    item.Outcome is ImportOutcome.Imported or ImportOutcome.Duplicate,
                    item.Message ?? DescribeOutcome(item.Outcome),
                    item.PasswordRetryToken))
            .ToArray();
        var skipped = allItems.Count(
            item => item.Outcome is ImportOutcome.Duplicate or ImportOutcome.Unsupported);
        var cancelled = allItems.Any(item => item.Outcome == ImportOutcome.Cancelled);

        return new UiImportBatchResult(importedBooks, results, skipped, cancelled);
    }

    public async Task<UiImportBatchResult> RetryWithPasswordAsync(
        string passwordRetryToken,
        string password,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var coreProgress = new Progress<BookImportProgress>(
            item =>
            {
                var fraction = item.Fraction ?? 0;
                progress.Report(
                    new ImportProgress(
                        item.CurrentItem ?? item.Stage,
                        Completed: (int)Math.Round(fraction * 100),
                        Total: 100,
                        Imported: 0,
                        Skipped: 0,
                        Failed: 0,
                        IsScanning: item.Fraction is null));
            });
        var item = await _importer
            .RetryWithPasswordAsync(
                passwordRetryToken,
                password,
                coreProgress,
                cancellationToken)
            .ConfigureAwait(false);

        var importedBooks = new List<BookDescriptor>();
        if (item.Outcome == ImportOutcome.Imported && item.BookId is { } bookId)
        {
            var book = await _repository
                .GetBookAsync(bookId, cancellationToken)
                .ConfigureAwait(false);
            if (book is not null)
            {
                importedBooks.Add(MapBook(book));
            }
        }

        var result = new ImportResultItemViewModel(
            item.DisplayName,
            item.Outcome is ImportOutcome.Imported or ImportOutcome.Duplicate,
            item.Message ?? DescribeOutcome(item.Outcome),
            item.PasswordRetryToken);
        return new UiImportBatchResult(
            importedBooks,
            new[] { result },
            item.Outcome is ImportOutcome.Duplicate or ImportOutcome.Unsupported ? 1 : 0,
            item.Outcome == ImportOutcome.Cancelled);
    }

    public async Task<ReaderDocument> OpenAsync(
        BookDescriptor book,
        CancellationToken cancellationToken)
    {
        return await OpenCoreAsync(book, password: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReaderDocument> OpenWithPasswordAsync(
        BookDescriptor book,
        string password,
        CancellationToken cancellationToken)
    {
        return await OpenCoreAsync(book, password, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReaderDocument> OpenCoreAsync(
        BookDescriptor book,
        string? password,
        CancellationToken cancellationToken)
    {
        var libraryBook = await _repository
            .GetBookAsync(book.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                $"The library entry for '{book.Title}' no longer exists.");

        BookDocument document;
        try
        {
            document = await _documents
                .OpenAsync(
                    libraryBook,
                    password,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FormatImportException exception) when (
            exception.Outcome == ImportOutcome.PasswordRequired)
        {
            throw new ReaderPasswordRequiredException(
                exception.Message,
                exception);
        }

        _openDocuments.Set(book.Id, document);
        var readerDocument = await ReaderDocumentMapper
            .MapAsync(
                document,
                libraryBook.ManagedPath,
                _pdfRenderer,
                password,
                cancellationToken)
            .ConfigureAwait(false);
        if (readerDocument.EstimatedWordCount > 0
            && readerDocument.EstimatedWordCount != libraryBook.EstimatedWordCount)
        {
            await _repository.SaveEstimatedWordCountAsync(
                    book.Id,
                    readerDocument.EstimatedWordCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return readerDocument;
    }

    public Task SetFavoriteAsync(
        Guid bookId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        return _repository.SetFavoriteAsync(bookId, isFavorite, cancellationToken);
    }

    public Task SaveEstimatedWordCountAsync(
        Guid bookId,
        long estimatedWordCount,
        CancellationToken cancellationToken = default)
    {
        return _repository.SaveEstimatedWordCountAsync(
            bookId,
            estimatedWordCount,
            cancellationToken);
    }

    public async Task UpdateMetadataAsync(
        Guid bookId,
        string title,
        string author,
        string category,
        CancellationToken cancellationToken = default)
    {
        await _repository
            .UpdateMetadataAsync(bookId, title, author, cancellationToken)
            .ConfigureAwait(false);

        var categoryName = string.IsNullOrWhiteSpace(category)
            ? "Uncategorised"
            : category.Trim();
        var categories = await _repository
            .GetCategoriesAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedCategory = categories.FirstOrDefault(
            item => string.Equals(
                item.Name,
                categoryName,
                StringComparison.CurrentCultureIgnoreCase));
        selectedCategory ??= await _repository
            .CreateCategoryAsync(categoryName, cancellationToken)
            .ConfigureAwait(false);

        await _repository
            .SetBookCategoriesAsync(
                bookId,
                new[] { selectedCategory.Id },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var categories = await _repository
            .GetCategoriesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (categories.Any(
                category => string.Equals(
                    category.Name,
                    name,
                    StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        _ = await _repository
            .CreateCategoryAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveReadingProgressAsync(
        Guid bookId,
        double progress,
        string locator,
        CancellationToken cancellationToken = default)
    {
        await _positionWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var book = await _repository
                .GetBookAsync(bookId, cancellationToken)
                .ConfigureAwait(false);
            _openDocuments.TryGet(bookId, out var document);
            var anchor = CreatePositionAnchor(
                bookId,
                document,
                locator,
                book?.ContentHash ?? string.Empty);
            var pageCount = document is FixedPageDocument fixedPage
                ? fixedPage.Pages.Length
                : book?.ReadingPosition?.PageCount;
            var pageNumber = anchor is PdfAnchor pdf ? pdf.PageIndex + 1 : (int?)null;
            var savedProgress = Math.Clamp(progress, 0, 1);
            if (anchor is PdfAnchor pageAnchor
                && pageCount is > 0
                && ReaderPositionLocator.TryParsePdf(locator, out _, out var withinPage))
            {
                savedProgress = Math.Clamp(
                    (pageAnchor.PageIndex + withinPage) / pageCount.Value,
                    0,
                    1);
            }

            await _repository
                .SaveReadingPositionAsync(
                    new ReadingPosition(
                        bookId,
                        Anchor: anchor,
                        Progress: savedProgress,
                        ChapterLabel: null,
                        PageNumber: pageNumber,
                        PageCount: pageCount,
                        UpdatedAt: DateTimeOffset.UtcNow,
                        LastSpokenPosition: book?.ReadingPosition?.LastSpokenPosition),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _positionWriteLock.Release();
        }
    }

    public async Task SaveSpeechPositionAsync(
        Guid bookId,
        string locator,
        int sentenceIndex,
        int characterOffset,
        CancellationToken cancellationToken = default)
    {
        await _positionWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var book = await _repository
                .GetBookAsync(bookId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The spoken book is no longer in the library.");
            _openDocuments.TryGet(bookId, out var document);
            var anchor = CreateSpeechAnchor(
                bookId,
                document,
                locator,
                Math.Max(0, characterOffset),
                book.ContentHash);
            var existing = book.ReadingPosition;
            await _repository
                .SaveReadingPositionAsync(
                    new ReadingPosition(
                        bookId,
                        Anchor: existing?.Anchor,
                        Progress: existing?.ClampedProgress ?? book.Progress,
                        ChapterLabel: existing?.ChapterLabel,
                        PageNumber: existing?.PageNumber,
                        PageCount: existing?.PageCount,
                        UpdatedAt: DateTimeOffset.UtcNow,
                        LastSpokenPosition: new SpeechPosition(
                            anchor,
                            Math.Max(0, sentenceIndex),
                            Math.Max(0, characterOffset))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _positionWriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<AnnotationItemViewModel>> LoadAnnotationsAsync(
        Guid bookId,
        CancellationToken cancellationToken = default)
    {
        var annotations = await _repository
            .GetAnnotationsAsync(bookId, cancellationToken)
            .ConfigureAwait(false);
        return annotations.Select(MapAnnotation).ToArray();
    }

    public async Task SaveAnnotationAsync(
        Guid bookId,
        AnnotationItemViewModel annotation,
        CancellationToken cancellationToken = default)
    {
        var book = await _repository
            .GetBookAsync(bookId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The annotated book is no longer in the library.");
        _openDocuments.TryGet(bookId, out var document);
        var revision = document?.RevisionHash ?? book.ContentHash;
        var anchor = CreateAnchor(bookId, revision, document, annotation);
        var kind = annotation.Kind switch
        {
            UiAnnotationKind.Bookmark => CoreAnnotationKind.Bookmark,
            UiAnnotationKind.Note => CoreAnnotationKind.Note,
            _ => CoreAnnotationKind.Highlight
        };

        await _repository
            .SaveAnnotationAsync(
                new Annotation(
                    annotation.Id,
                    bookId,
                    kind,
                    anchor,
                    annotation.CreatedAt,
                    DateTimeOffset.UtcNow,
                    Text: string.IsNullOrWhiteSpace(annotation.Note)
                        ? null
                        : annotation.Note,
                    Label: kind == CoreAnnotationKind.Bookmark
                        ? annotation.Title
                        : string.IsNullOrWhiteSpace(annotation.Excerpt)
                            ? annotation.Title
                            : annotation.Excerpt,
                    Color: kind == CoreAnnotationKind.Highlight
                        ? ParseHighlightColor(annotation.HighlightColorName)
                        : null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task DeleteAnnotationAsync(
        Guid annotationId,
        CancellationToken cancellationToken = default)
    {
        return _repository.DeleteAnnotationAsync(annotationId, cancellationToken);
    }

    public async Task<UiApplicationSettings> LoadUiSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _repository
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        return new UiApplicationSettings(
            settings.LibraryTheme.ToString(),
            settings.Reader.Theme == OpenShelf.Core.ReaderTheme.Dark
                ? "Midnight"
                : settings.Reader.Theme.ToString(),
            settings.Reader.LayoutMode.ToString(),
            settings.Reader.FontFamily,
            settings.Reader.FontSize,
            settings.Reader.LineHeight,
            settings.Reader.ParagraphSpacing,
            settings.Reader.HorizontalMargin,
            settings.Reader.ColumnWidth,
            settings.Reader.Alignment.ToString(),
            settings.SpeechVoice,
            settings.SpeechRate,
            settings.HighlightSpokenSentence,
            settings.FollowSpokenSentence,
            settings.SpeechVolume,
            settings.SpeechPitch,
            settings.PersonalReadingWordsPerMinute);
    }

    public async Task SaveUiSettingsAsync(
        UiApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var current = await _repository
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        var libraryTheme = Enum.TryParse<LibraryTheme>(
            settings.LibraryTheme,
            ignoreCase: true,
            out var parsedTheme)
            ? parsedTheme
            : LibraryTheme.Dark;
        var layoutMode = Enum.TryParse<ReaderLayoutMode>(
            settings.ReaderLayoutMode,
            ignoreCase: true,
            out var parsedLayout)
            ? parsedLayout
            : ReaderLayoutMode.Scroll;
        var readerThemeName = string.Equals(
            settings.ReaderTheme,
            "Midnight",
            StringComparison.OrdinalIgnoreCase)
            ? "Dark"
            : settings.ReaderTheme;
        var readerTheme = Enum.TryParse<OpenShelf.Core.ReaderTheme>(
            readerThemeName,
            ignoreCase: true,
            out var parsedReaderTheme)
            ? parsedReaderTheme
            : OpenShelf.Core.ReaderTheme.Dark;
        var alignment = Enum.TryParse<TextAlignmentKind>(
            settings.Alignment,
            ignoreCase: true,
            out var parsedAlignment)
            ? parsedAlignment
            : TextAlignmentKind.Start;
        await _repository
            .SaveSettingsAsync(
                current with
                {
                    LibraryTheme = libraryTheme,
                    Reader = current.Reader with
                    {
                        Theme = readerTheme,
                        LayoutMode = layoutMode,
                        FontFamily = settings.FontFamily,
                        FontSize = settings.FontSize,
                        LineHeight = settings.LineHeight,
                        ParagraphSpacing = settings.ParagraphSpacing,
                        HorizontalMargin = settings.HorizontalMargin,
                        ColumnWidth = settings.ColumnWidth,
                        Alignment = alignment
                    },
                    SpeechVoice = settings.SpeechVoice,
                    SpeechRate = settings.SpeechRate,
                    HighlightSpokenSentence = settings.HighlightSpokenSentence,
                    FollowSpokenSentence = settings.FollowSpokenSentence,
                    SpeechVolume = settings.SpeechVolume,
                    SpeechPitch = settings.SpeechPitch,
                    PersonalReadingWordsPerMinute = settings.PersonalReadingWordsPerMinute
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IProgress<ImportBatchProgress> CreateImportProgress(
        IProgress<ImportProgress> progress)
    {
        return new Progress<ImportBatchProgress>(
            item => progress.Report(
                new ImportProgress(
                    item.CurrentFile is null
                        ? "Preparing import…"
                        : Path.GetFileName(item.CurrentFile),
                    item.Processed,
                    item.Total,
                    item.Imported,
                    item.Duplicates,
                    item.Failed,
                    IsScanning: item.Total == 0)));
    }

    private static BookDescriptor MapBook(LibraryBook book)
    {
        return new BookDescriptor(
            book.Id,
            book.DisplayTitle,
            book.DisplayAuthor,
            book.Format.ToString().ToUpperInvariant(),
            book.ManagedPath,
            Path.GetDirectoryName(book.ManagedPath) ?? string.Empty,
            book.Categories.FirstOrDefault()?.Name ?? "Uncategorised",
            DecodeImage(book.CoverImage),
            book.Progress,
            book.IsFavorite,
            MapReadingLocator(book.ReadingPosition),
            book.ImportedAt,
            MapBaseAnchorLocator(book.ReadingPosition?.LastSpokenPosition?.Anchor),
            book.ReadingPosition?.LastSpokenPosition?.SentenceIndex ?? 0,
            book.ReadingPosition?.LastSpokenPosition?.CharacterOffset ?? 0,
            book.EstimatedWordCount);
    }

    internal static AnnotationItemViewModel MapAnnotation(Annotation annotation)
    {
        var kind = annotation.Kind switch
        {
            CoreAnnotationKind.Bookmark => UiAnnotationKind.Bookmark,
            CoreAnnotationKind.Note => UiAnnotationKind.Note,
            _ => UiAnnotationKind.Highlight
        };
        var locator = annotation.Anchor switch
        {
            ReflowableAnchor reflowable =>
                $"{reflowable.SectionId}|{reflowable.BlockId}",
            PdfAnchor pdf => ReaderPositionLocator.FormatPdf(
                pdf.PageIndex,
                pdf.WithinPageOffset),
            _ => string.Empty
        };
        var excerpt = annotation.Anchor switch
        {
            ReflowableAnchor reflowable => reflowable.ExactQuote,
            PdfAnchor pdf => pdf.ExactQuote,
            _ => null
        };

        return new AnnotationItemViewModel(
            annotation.Id,
            kind,
            locator,
            kind switch
            {
                UiAnnotationKind.Bookmark => annotation.Label ?? "Bookmark",
                UiAnnotationKind.Note => "Note",
                _ => "Highlight"
            },
            excerpt ?? annotation.Label ?? string.Empty,
            annotation.Text ?? string.Empty,
            annotation.CreatedAt,
            annotation.Anchor switch
            {
                ReflowableAnchor reflowable => reflowable.StartOffset,
                PdfAnchor pdf => pdf.StartCharacter,
                _ => 0
            },
            annotation.Anchor switch
            {
                ReflowableAnchor reflowable =>
                    Math.Max(0, reflowable.EndOffset - reflowable.StartOffset),
                PdfAnchor pdf => pdf.CharacterLength,
                _ => 0
            },
            annotation.Color?.ToString() ?? "Yellow");
    }

    internal static DocumentAnchor CreateAnchor(
        Guid bookId,
        string revision,
        BookDocument? document,
        AnnotationItemViewModel annotation)
    {
        if (document is FixedPageDocument
            || annotation.Locator.StartsWith("page:", StringComparison.Ordinal))
        {
            _ = ReaderPositionLocator.TryParsePdf(
                annotation.Locator,
                out var pageIndex,
                out var withinPage);

            return new PdfAnchor(
                bookId,
                revision,
                Math.Max(0, pageIndex),
                StartCharacter: annotation.AnchorStart,
                CharacterLength: annotation.AnchorLength,
                Rectangles: ImmutableArray<DocumentRectangle>.Empty,
                ExactQuote: EmptyToNull(annotation.Excerpt),
                WithinPageOffset: withinPage);
        }

        _ = ReaderPositionLocator.TryParseReflowable(
            annotation.Locator,
            out var reflowableLocator,
            out _);
        var locatorParts = reflowableLocator.Split('|', 2);
        return new ReflowableAnchor(
            bookId,
            revision,
            locatorParts.Length == 2 ? locatorParts[0] : string.Empty,
            locatorParts.Length == 2 ? locatorParts[1] : annotation.Locator,
            StartOffset: annotation.AnchorStart,
            EndOffset: annotation.AnchorStart + annotation.AnchorLength,
            ExactQuote: EmptyToNull(annotation.Excerpt));
    }

    internal static DocumentAnchor? CreatePositionAnchor(
        Guid bookId,
        BookDocument? document,
        string locator,
        string revisionFallback)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return null;
        }

        var revision = document?.RevisionHash ?? revisionFallback;

        if (document is FixedPageDocument
            || locator.StartsWith("page:", StringComparison.Ordinal))
        {
            _ = ReaderPositionLocator.TryParsePdf(
                locator,
                out var pageIndex,
                out var withinPage);

            return new PdfAnchor(
                bookId,
                revision,
                Math.Max(0, pageIndex),
                0,
                0,
                ImmutableArray<DocumentRectangle>.Empty,
                WithinPageOffset: withinPage);
        }

        _ = ReaderPositionLocator.TryParseReflowable(
            locator,
            out var baseLocator,
            out var characterOffset);
        var parts = baseLocator.Split('|', 2);
        return new ReflowableAnchor(
            bookId,
            revision,
            parts.Length == 2 ? parts[0] : string.Empty,
            parts.Length == 2 ? parts[1] : locator,
            characterOffset,
            characterOffset);
    }

    private static DocumentAnchor CreateSpeechAnchor(
        Guid bookId,
        BookDocument? document,
        string locator,
        int characterOffset,
        string revisionFallback)
    {
        var revision = document?.RevisionHash ?? revisionFallback;
        if (document is FixedPageDocument
            || locator.StartsWith("page:", StringComparison.Ordinal))
        {
            _ = ReaderPositionLocator.TryParsePdf(
                locator,
                out var pageIndex,
                out var withinPage);
            return new PdfAnchor(
                bookId,
                revision,
                pageIndex,
                StartCharacter: characterOffset,
                CharacterLength: 0,
                Rectangles: ImmutableArray<DocumentRectangle>.Empty,
                WithinPageOffset: withinPage);
        }

        _ = ReaderPositionLocator.TryParseReflowable(
            locator,
            out var baseLocator,
            out _);
        var parts = baseLocator.Split('|', 2);
        return new ReflowableAnchor(
            bookId,
            revision,
            parts.Length == 2 ? parts[0] : string.Empty,
            parts.Length == 2 ? parts[1] : locator,
            StartOffset: characterOffset,
            EndOffset: characterOffset);
    }

    internal static string MapReadingLocator(ReadingPosition? position)
    {
        if (position?.Anchor is ReflowableAnchor reflowable)
        {
            return ReaderPositionLocator.FormatReflowable(
                $"{reflowable.SectionId}|{reflowable.BlockId}",
                reflowable.StartOffset);
        }

        if (position?.Anchor is not PdfAnchor pdf)
        {
            return string.Empty;
        }

        var pageCount = Math.Max(1, position.PageCount ?? 1);
        var derivedWithinPage = Math.Clamp(
            position.ClampedProgress * pageCount - pdf.PageIndex,
            0,
            1);
        var withinPage = pdf.WithinPageOffset > 0
            ? pdf.WithinPageOffset
            : derivedWithinPage;
        return ReaderPositionLocator.FormatPdf(pdf.PageIndex, withinPage);
    }

    private static string MapBaseAnchorLocator(DocumentAnchor? anchor) => anchor switch
    {
        ReflowableAnchor reflowable =>
            $"{reflowable.SectionId}|{reflowable.BlockId}",
        PdfAnchor pdf => $"page:{pdf.PageIndex}",
        _ => string.Empty
    };

    private static Bitmap? DecodeImage(byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeOutcome(ImportOutcome outcome) => outcome switch
    {
        ImportOutcome.Imported => "Ready in your library.",
        ImportOutcome.Duplicate => "Already in your library.",
        ImportOutcome.Unsupported => "Unsupported file format.",
        ImportOutcome.PasswordRequired => "A password is required.",
        ImportOutcome.DrmProtected => "DRM-protected books cannot be opened.",
        ImportOutcome.Corrupt => "The book appears to be damaged.",
        ImportOutcome.Cancelled => "Import cancelled.",
        _ => "The book could not be imported."
    };

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static HighlightColor ParseHighlightColor(string value) =>
        Enum.TryParse<HighlightColor>(
            value,
            ignoreCase: true,
            out var color)
            ? color
            : HighlightColor.Yellow;
}

internal static class ReaderDocumentMapper
{
    public static Task<ReaderDocument> MapAsync(
        BookDocument document,
        string managedPath,
        PdfPageRenderer pdfRenderer,
        string? password,
        CancellationToken cancellationToken)
    {
        return document switch
        {
            ReflowableDocument reflowable =>
                Task.FromResult(MapReflowable(reflowable)),
            FixedPageDocument fixedPage =>
                Task.FromResult(
                    MapFixedPage(
                        fixedPage,
                        managedPath,
                        pdfRenderer,
                        password)),
            _ => Task.FromResult(
                new ReaderDocument(
                    Array.Empty<ReaderBlockViewModel>(),
                    Array.Empty<TocItemViewModel>()))
        };
    }

    private static ReaderDocument MapReflowable(ReflowableDocument document)
    {
        var blocks = new List<ReaderBlockViewModel>();
        var resources = new BookResourceStore(document);
        foreach (var section in document.Sections)
        {
            foreach (var block in section.Blocks)
            {
                AppendBlock(
                    blocks,
                    section.Id,
                    block,
                    resources);
            }
        }

        var toc = new List<TocItemViewModel>();
        AppendToc(
            toc,
            document.TableOfContents,
            level: 1,
            document.Sections);

        if (toc.Count == 0)
        {
            for (var index = 0; index < document.Sections.Length; index++)
            {
                var section = document.Sections[index];
                if (!string.IsNullOrWhiteSpace(section.Title))
                {
                    toc.Add(
                        new TocItemViewModel(
                            section.Title,
                            $"{section.Id}|",
                            1,
                            document.Sections.Length == 0
                                ? 0
                                : (double)index / document.Sections.Length));
                }
            }
        }

        return new ReaderDocument(
            blocks,
            toc,
            EstimatedWordCount: EstimateWordCount(document.NormalizedLength));
    }

    private static ReaderDocument MapFixedPage(
        FixedPageDocument document,
        string managedPath,
        PdfPageRenderer pdfRenderer,
        string? password)
    {
        var blocks = new List<ReaderBlockViewModel>();
        foreach (var page in document.Pages)
        {
            var locator = $"page:{page.Index}";
            var pageIndex = page.Index;
            blocks.Add(
                new ReaderImageBlockViewModel(
                    locator,
                    cancellationToken => LoadPdfPageAsync(
                        managedPath,
                        document.RevisionHash,
                        pageIndex,
                        pdfRenderer,
                        password,
                        cancellationToken),
                    $"Page {pageIndex + 1}",
                    $"Page {pageIndex + 1}"));

            if (!string.IsNullOrWhiteSpace(page.PlainText))
            {
                blocks.Add(
                    new ReaderParagraphBlockViewModel(
                        locator,
                        page.PlainText));
            }
            else
            {
                blocks.Add(
                    new ReaderNoticeBlockViewModel(
                        locator,
                        "This page has no selectable text layer. Copy, highlighting, notes, and text to speech are unavailable for its image content."));
            }
        }

        var toc = document.TableOfContents
            .Select(
                (item, index) => new TocItemViewModel(
                    item.Title,
                    item.TargetSectionId,
                    1,
                    document.TableOfContents.Length == 0
                        ? 0
                        : (double)index / document.TableOfContents.Length))
            .ToArray();
        return new ReaderDocument(
            blocks,
            toc,
            IsFixedPage: true,
            PageCount: document.Pages.Length,
            EstimatedWordCount: EstimateWordCount(document.NormalizedLength));
    }

    private static long EstimateWordCount(long normalizedLength) =>
        normalizedLength <= 0 ? 0 : Math.Max(1, normalizedLength / 6);

    private static async Task<IImage?> LoadPdfPageAsync(
        string managedPath,
        string revisionHash,
        int pageIndex,
        PdfPageRenderer pdfRenderer,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            var png = await pdfRenderer
                .RenderPagePngAsync(
                    managedPath,
                    revisionHash,
                    pageIndex,
                    dpi: 144,
                    password,
                    cancellationToken)
                .ConfigureAwait(false);
            using var stream = new MemoryStream(png, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static void AppendBlock(
        ICollection<ReaderBlockViewModel> target,
        string sectionId,
        DocumentBlock block,
        BookResourceStore resources)
    {
        var locator = $"{sectionId}|{block.Id}";
        switch (block)
        {
            case HeadingBlock heading:
                var headingText = DocumentText.Flatten(heading.Content);
                if (!string.IsNullOrWhiteSpace(headingText))
                {
                    target.Add(
                        new ReaderHeadingBlockViewModel(locator, headingText));
                }

                AppendInlineImages(
                    target,
                    locator,
                    heading.Content,
                    resources);
                break;

            case ParagraphBlock paragraph:
                var paragraphText = DocumentText.Flatten(paragraph.Content);
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    target.Add(
                        new ReaderParagraphBlockViewModel(locator, paragraphText));
                }

                AppendInlineImages(
                    target,
                    locator,
                    paragraph.Content,
                    resources);
                break;

            case OpenShelf.Core.ImageBlock image:
                target.Add(CreateImageBlock(locator, image, resources));
                break;

            case QuoteBlock quote:
                foreach (var nested in quote.Blocks)
                {
                    AppendBlock(target, sectionId, nested, resources);
                }

                if (!string.IsNullOrWhiteSpace(quote.Attribution))
                {
                    target.Add(
                        new ReaderParagraphBlockViewModel(
                            $"{locator}:attribution",
                            $"— {quote.Attribution}"));
                }

                break;

            case ListBlock list:
                foreach (var item in list.Items)
                {
                    foreach (var nested in item.Blocks)
                    {
                        AppendBlock(target, sectionId, nested, resources);
                    }
                }

                break;

            case TableBlock table:
                foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                {
                    foreach (var nested in cell.Blocks)
                    {
                        AppendBlock(target, sectionId, nested, resources);
                    }
                }

                break;

            case PageBreakBlock pageBreak when !string.IsNullOrWhiteSpace(pageBreak.Label):
                target.Add(
                    new ReaderHeadingBlockViewModel(locator, pageBreak.Label));
                break;

            case HorizontalRuleBlock:
                target.Add(new ReaderParagraphBlockViewModel(locator, "—"));
                break;
        }
    }

    private static void AppendInlineImages(
        ICollection<ReaderBlockViewModel> target,
        string locator,
        IEnumerable<InlineContent> content,
        BookResourceStore resources)
    {
        var index = 0;
        foreach (var inlineImage in content.OfType<InlineImage>())
        {
            var resourceId = inlineImage.ResourceId;
            target.Add(
                new ReaderImageBlockViewModel(
                    $"{locator}:image:{index++}",
                    cancellationToken => DecodeResourceAsync(
                        resources,
                        resourceId,
                        cancellationToken),
                    inlineImage.AlternativeText ?? "Book image"));
        }
    }

    private static ReaderImageBlockViewModel CreateImageBlock(
        string locator,
        OpenShelf.Core.ImageBlock image,
        BookResourceStore resources)
    {
        var resourceId = image.ResourceId;
        return new ReaderImageBlockViewModel(
            locator,
            cancellationToken => DecodeResourceAsync(
                resources,
                resourceId,
                cancellationToken),
            image.AlternativeText ?? "Book image",
            image.Caption);
    }

    private static Task<IImage?> DecodeResourceAsync(
        BookResourceStore resources,
        string resourceId,
        CancellationToken cancellationToken)
    {
        return Task.Run<IImage?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resources.TryGet(resourceId, out var resource)
                    || resource is null
                    || resource.Data.Length == 0)
                {
                    return null;
                }

                try
                {
                    if (string.Equals(
                            resource.MediaType,
                            "image/svg+xml",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetExtension(resource.OriginalPath),
                            ".svg",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (SafeSvgRasterizer.TryRasterize(
                                resource.Data,
                                out var svgBitmap,
                                out _,
                                cancellationToken: cancellationToken))
                        {
                            return svgBitmap;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        return null;
                    }

                    using var stream = new MemoryStream(resource.Data, writable: false);
                    return new Bitmap(stream);
                }
                catch
                {
                    return null;
                }
            },
            cancellationToken);
    }

    private static void AppendToc(
        ICollection<TocItemViewModel> target,
        IEnumerable<TableOfContentsItem> items,
        int level,
        IReadOnlyList<DocumentSection> sections)
    {
        foreach (var item in items)
        {
            var sectionIndex = sections
                .Select((section, index) => (section, index))
                .FirstOrDefault(pair => pair.section.Id == item.TargetSectionId)
                .index;
            target.Add(
                new TocItemViewModel(
                    item.Title,
                    $"{item.TargetSectionId}|{item.TargetBlockId}",
                    level,
                    sections.Count == 0
                        ? 0
                        : (double)sectionIndex / sections.Count));
            AppendToc(target, item.Children, level + 1, sections);
        }
    }
}

internal sealed class ProductionTextToSpeechGateway :
    ITextToSpeechGateway,
    IProgressTextToSpeechGateway
{
    private readonly ISpeechEngine _engine;

    public ProductionTextToSpeechGateway(ISpeechEngine engine)
    {
        _engine = engine;
        _engine.ProgressChanged += Engine_ProgressChanged;
    }

    public event EventHandler<TextToSpeechProgress>? ProgressChanged;

    public IReadOnlyList<SpeechVoiceOption> Voices =>
        _engine.Voices
            .Where(voice => voice.IsAvailable)
            .Select(MapVoice)
            .ToArray();

    public string ProviderStatus
    {
        get
        {
            var windows = _engine.Voices.Count(
                voice => voice.Provider is SpeechProvider.WindowsModern
                    or SpeechProvider.WindowsSapi);
            var builtIn = _engine.Voices.Count(
                voice => voice.Provider == SpeechProvider.BuiltIn);
            return windows > 0
                ? $"{windows} Windows voice{(windows == 1 ? string.Empty : "s")} and {builtIn} built-in fallback voice{(builtIn == 1 ? string.Empty : "s")} available."
                : $"Windows voices are unavailable; {builtIn} built-in fallback voice{(builtIn == 1 ? string.Empty : "s")} available.";
        }
    }

    public string PeterStatus => _engine.Voices.Any(
        voice => voice.IsAvailable
            && voice.DisplayName.Contains("Peter", StringComparison.OrdinalIgnoreCase))
        ? "Acapela — Peter is ready and will be preferred. OpenShelf is using your installed licensed copy."
        : "Peter is not currently exposed through Windows SAPI. If Peter works only in Communicator 5, its licensed voice package may be private to Communicator or may need its supported SAPI installation repaired.";

    public Task RefreshVoicesAsync(CancellationToken cancellationToken) =>
        _engine.RefreshVoicesAsync(cancellationToken);

    public Task PreviewAsync(
        string voiceId,
        double rate,
        int volume,
        double pitch,
        CancellationToken cancellationToken) =>
        _engine.PreviewAsync(
            CreateOptions(voiceId, rate, volume, pitch),
            "This is the selected OpenShelf reading voice.",
            cancellationToken);

    public async Task SpeakAsync(
        IReadOnlyList<string> segments,
        string voiceId,
        double rate,
        int volume,
        double pitch,
        int startIndex,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return;
        }

        await _engine
            .SpeakAsync(
                segments,
                CreateOptions(voiceId, rate, volume, pitch),
                startIndex: Math.Clamp(startIndex, 0, segments.Count - 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Pause()
    {
        _engine.PauseAsync().GetAwaiter().GetResult();
    }

    public void Resume()
    {
        _engine.ResumeAsync().GetAwaiter().GetResult();
    }

    public void Stop()
    {
        _engine.StopAsync().GetAwaiter().GetResult();
    }

    public void Previous()
    {
        _engine.PreviousAsync().GetAwaiter().GetResult();
    }

    public void Next()
    {
        _engine.NextAsync().GetAwaiter().GetResult();
    }

    private void Engine_ProgressChanged(object? sender, SpeechProgress progress)
    {
        ProgressChanged?.Invoke(
            this,
            new TextToSpeechProgress(
                progress.SegmentIndex,
                progress.SegmentCount,
                progress.Text));
    }

    private static SpeechOptions CreateOptions(
        string voiceId,
        double rate,
        int volume,
        double pitch) =>
        new SpeechOptions(
            voiceId,
            Math.Clamp((int)Math.Round(175 * rate), 80, 450),
            volume,
            pitch).Clamped;

    private static SpeechVoiceOption MapVoice(SpeechVoice voice) =>
        new(
            voice.Id,
            voice.DisplayName,
            voice.Language,
            voice.Provider switch
            {
                SpeechProvider.WindowsModern => "Microsoft modern",
                SpeechProvider.WindowsSapi => "Windows SAPI",
                _ => "Built-in"
            },
            voice.Architecture.ToString(),
            voice.IsDefault,
            voice.SupportsRate,
            voice.SupportsVolume,
            voice.SupportsPitch);
}
