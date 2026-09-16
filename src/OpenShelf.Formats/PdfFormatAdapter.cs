using System.Collections.Concurrent;
using System.Collections.Immutable;
using OpenShelf.Core;
using PDFtoImage;
using UglyToad.PdfPig;

namespace OpenShelf.Formats;

public sealed class PdfFormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private readonly PdfPageRenderer renderer;

    public PdfFormatAdapter()
        : this(new PdfPageRenderer())
    {
    }

    public PdfFormatAdapter(PdfPageRenderer renderer)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public BookFormat Format => BookFormat.Pdf;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith("%PDF-"u8);

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Opening PDF", request.DisplayName, 0, null, 0));

        try
        {
            var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
                request.FilePath,
                cancellationToken).ConfigureAwait(false);
            var parsingOptions = new ParsingOptions();
            if (!string.IsNullOrEmpty(request.Password))
            {
                parsingOptions.Password = request.Password;
            }

            using var document = PdfDocument.Open(request.FilePath, parsingOptions);
            if (document.NumberOfPages <= 0)
            {
                throw FormatImportException.Corrupt("The PDF contains no pages.");
            }

            var metadata = new BookMetadata(
                Clean(document.Information.Title) ?? FormatUtilities.FallbackTitle(request),
                Clean(document.Information.Author) ?? "Unknown author",
                Clean(document.Information.Subject),
                null,
                null,
                null);
            var pages = ImmutableArray.CreateBuilder<FixedPage>(document.NumberOfPages);
            long normalizedLength = 0;
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new BookImportProgress(
                    "Extracting PDF text",
                    $"Page {pageNumber}",
                    pageNumber - 1,
                    document.NumberOfPages,
                    (double)(pageNumber - 1) / document.NumberOfPages));
                var page = document.GetPage(pageNumber);
                var glyphs = ImmutableArray.CreateBuilder<TextGlyph>(page.Letters.Count);
                var text = new System.Text.StringBuilder();
                foreach (var letter in page.Letters)
                {
                    var characterIndex = text.Length;
                    text.Append(letter.Value);
                    var rectangle = letter.BoundingBox;
                    var pageHeight = Convert.ToDouble(page.Height);
                    glyphs.Add(new TextGlyph(
                        characterIndex,
                        letter.Value,
                        Convert.ToDouble(rectangle.Left),
                        pageHeight - Convert.ToDouble(rectangle.Top),
                        Convert.ToDouble(rectangle.Right),
                        pageHeight - Convert.ToDouble(rectangle.Bottom)));
                }

                var plainText = text.ToString();
                normalizedLength += plainText.Length;
                pages.Add(new FixedPage(
                    pageNumber - 1,
                    Convert.ToDouble(page.Width),
                    Convert.ToDouble(page.Height),
                    plainText,
                    glyphs.ToImmutable(),
                    PdfPageRenderer.CreateCacheKey(revisionHash, pageNumber - 1, 144)));
            }

            var warnings = ImmutableArray.CreateBuilder<string>();
            if (pages.All(page => string.IsNullOrWhiteSpace(page.PlainText)))
            {
                warnings.Add(
                    "This PDF has no extractable text layer; search, copy and speech require OCR.");
            }

            byte[]? cover = null;
            try
            {
                cover = await renderer.RenderPagePngAsync(
                    request.FilePath,
                    revisionHash,
                    0,
                    120,
                    request.Password,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add($"The PDF cover could not be rendered: {exception.Message}");
            }

            var toc = pages.Select(page => new TableOfContentsItem(
                FormatUtilities.StableId(
                    "toc",
                    revisionHash,
                    page.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                $"Page {page.Index + 1}",
                $"page-{page.Index}",
                null,
                [])).ToImmutableArray();
            var fixedDocument = new FixedPageDocument(
                metadata,
                revisionHash,
                normalizedLength,
                pages.ToImmutable(),
                toc);
            progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));
            return new ImportedBook(
                Format,
                metadata,
                fixedDocument,
                cover,
                cover is null ? null : "image/png",
                warnings.ToImmutable());
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPasswordFailure(exception))
        {
            throw FormatImportException.PasswordRequired(
                "The PDF is encrypted and requires a valid password.",
                exception);
        }
        catch (Exception exception)
        {
            throw FormatImportException.Corrupt("The PDF could not be parsed.", exception);
        }
    }

    private static bool IsPasswordFailure(Exception exception)
    {
        var text = $"{exception.GetType().Name} {exception.Message}";
        return text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? value)
    {
        var cleaned = FormatUtilities.CleanText(value);
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// Thread-safe bounded PDF page PNG cache. PDFtoImage serializes its native
/// PDFium calls; callers can safely request pages from multiple readers.
/// </summary>
public sealed class PdfPageRenderer
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> cache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> insertionOrder = new();
    private readonly int maximumCachedPages;

    public PdfPageRenderer(int maximumCachedPages = 24)
    {
        if (maximumCachedPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCachedPages));
        }

        this.maximumCachedPages = maximumCachedPages;
    }

    public static string CreateCacheKey(string revisionHash, int pageIndex, int dpi) =>
        $"{revisionHash}:page:{pageIndex}:dpi:{dpi}";

    public Task<byte[]> RenderPagePngAsync(
        string pdfPath,
        string revisionHash,
        int pageIndex,
        int dpi,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (dpi is < 36 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        var key = CreateCacheKey(revisionHash, pageIndex, dpi);
        var lazy = cache.GetOrAdd(key, _ =>
        {
            insertionOrder.Enqueue(key);
            return new Lazy<Task<byte[]>>(
                () => RenderCoreAsync(pdfPath, pageIndex, dpi, password, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
        });
        TrimCache();
        return lazy.Value;
    }

    public void Clear()
    {
        cache.Clear();
        while (insertionOrder.TryDequeue(out _))
        {
        }
    }

    private static Task<byte[]> RenderCoreAsync(
        string pdfPath,
        int pageIndex,
        int dpi,
        string? password,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() &&
                !OperatingSystem.IsLinux() &&
                !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException(
                    "PDF page rendering is supported on Windows, Linux and macOS.");
            }

            using var input = File.OpenRead(pdfPath);
            using var output = new MemoryStream();
#pragma warning disable CA1416 // Guarded above; the application targets desktop OSes.
            Conversion.SavePng(
                output,
                input,
                pageIndex,
                leaveOpen: false,
                password: password,
                options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
            cancellationToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }, cancellationToken);
    }

    private void TrimCache()
    {
        while (cache.Count > maximumCachedPages &&
               insertionOrder.TryDequeue(out var oldest))
        {
            cache.TryRemove(oldest, out _);
        }
    }
}
