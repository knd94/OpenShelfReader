using System.Collections.Immutable;
using OpenShelf.Core;
using OpenShelf.Formats;

namespace OpenShelf.Tests.Infrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"openshelf-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // A failed cleanup should not hide the assertion that preceded it.
        }
    }
}

internal sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}

internal sealed class RecordingTextAdapter : IBookFormatAdapter
{
    public BookFormat Format => BookFormat.Txt;

    public IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" };

    public List<BookImportRequest> Requests { get; } = [];

    public Action? ImportStarted { get; set; }

    public bool MatchesSignature(ReadOnlySpan<byte> header) => true;

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        ImportStarted?.Invoke();
        var text = await File.ReadAllTextAsync(request.FilePath, cancellationToken);
        var metadata = new BookMetadata(
            System.IO.Path.GetFileNameWithoutExtension(request.DisplayName),
            "Test Author");
        var document = new ReflowableDocument(
            metadata,
            request.RevisionHash,
            text.Length,
            ImmutableArray.Create(
                new DocumentSection(
                    "section-1",
                    "Chapter",
                    ImmutableArray.Create<DocumentBlock>(
                        new ParagraphBlock(
                            "paragraph-1",
                            ImmutableArray.Create<InlineContent>(new TextRun(text)))),
                    0,
                    text.Length)),
            ImmutableArray<TableOfContentsItem>.Empty,
            ImmutableDictionary<string, BookResource>.Empty);
        return new ImportedBook(
            BookFormat.Txt,
            metadata,
            document,
            CoverImage: null,
            CoverMediaType: null,
            ImmutableArray<string>.Empty);
    }
}

internal sealed class ControllablePdfAdapter : IBookFormatAdapter
{
    public BookFormat Format => BookFormat.Pdf;

    public IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public ImportOutcome? ForcedOutcome { get; set; }

    public string? RequiredPassword { get; set; }

    public int Attempts { get; private set; }

    public int AttemptsWithPassword { get; private set; }

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith("%PDF-"u8);

    public Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        if (request.Password is not null)
        {
            AttemptsWithPassword++;
        }

        if (ForcedOutcome is { } forcedOutcome)
        {
            throw new FormatImportException(
                forcedOutcome,
                $"Classified as {forcedOutcome}.");
        }

        if (RequiredPassword is not null
            && !string.Equals(
                request.Password,
                RequiredPassword,
                StringComparison.Ordinal))
        {
            throw FormatImportException.PasswordRequired(
                "The PDF requires a valid password.");
        }

        progress?.Report(new BookImportProgress(
            "Complete",
            request.DisplayName,
            1,
            1,
            1));
        var metadata = new BookMetadata("Protected PDF", "Test Author");
        var document = new FixedPageDocument(
            metadata,
            request.RevisionHash,
            0,
            ImmutableArray<FixedPage>.Empty,
            ImmutableArray<TableOfContentsItem>.Empty);
        return Task.FromResult(new ImportedBook(
            BookFormat.Pdf,
            metadata,
            document,
            CoverImage: null,
            CoverMediaType: null,
            ImmutableArray<string>.Empty));
    }
}
