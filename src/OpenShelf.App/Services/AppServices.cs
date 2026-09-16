using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using OpenShelf.Core;
using OpenShelf.Formats;
using OpenShelf.Infrastructure;
using OpenShelf.App.ViewModels;
using UiImportBatchResult = OpenShelf.App.ViewModels.ImportBatchResult;

namespace OpenShelf.App.Services;

public sealed class AppServices : IAsyncDisposable
{
    private readonly IReadOnlyList<IAsyncDisposable> _ownedServices;

    public AppServices(
        ILibraryGateway library,
        IReaderGateway reader,
        ITextToSpeechGateway speech,
        IDiagnosticsGateway diagnostics,
        int registryCount = 0,
        bool isPreview = false,
        IReadOnlyList<IAsyncDisposable>? ownedServices = null)
    {
        Library = library;
        Reader = reader;
        Speech = speech;
        Diagnostics = diagnostics;
        RegistryCount = registryCount;
        IsPreview = isPreview;
        _ownedServices = ownedServices ?? Array.Empty<IAsyncDisposable>();
    }

    public ILibraryGateway Library { get; }

    public IReaderGateway Reader { get; }

    public ITextToSpeechGateway Speech { get; }

    public IDiagnosticsGateway Diagnostics { get; }

    public int RegistryCount { get; }

    public bool IsPreview { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _ownedServices.Reverse())
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public interface ILibraryGateway
{
    Task<UiImportBatchResult> ImportAsync(
        IReadOnlyList<string> selectedPaths,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookDescriptor>> LoadLibraryAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookDescriptor>>(Array.Empty<BookDescriptor>());

    Task<UiImportBatchResult> RetryWithPasswordAsync(
        string passwordRetryToken,
        string password,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            new UiImportBatchResult(
                Array.Empty<BookDescriptor>(),
                new[]
                {
                    new ImportResultItemViewModel(
                        "Protected book",
                        false,
                        "Password retry is unavailable in preview mode.")
                },
                0,
                false));

    Task<IReadOnlyList<string>> LoadCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    Task SetFavoriteAsync(
        Guid bookId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task UpdateMetadataAsync(
        Guid bookId,
        string title,
        string author,
        string category,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task SaveReadingProgressAsync(
        Guid bookId,
        double progress,
        string locator,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task SaveEstimatedWordCountAsync(
        Guid bookId,
        long estimatedWordCount,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task SaveSpeechPositionAsync(
        Guid bookId,
        string locator,
        int sentenceIndex,
        int characterOffset,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task<IReadOnlyList<AnnotationItemViewModel>> LoadAnnotationsAsync(
        Guid bookId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AnnotationItemViewModel>>(
            Array.Empty<AnnotationItemViewModel>());

    Task SaveAnnotationAsync(
        Guid bookId,
        AnnotationItemViewModel annotation,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task DeleteAnnotationAsync(
        Guid annotationId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task<UiApplicationSettings> LoadUiSettingsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(UiApplicationSettings.Default);

    Task SaveUiSettingsAsync(
        UiApplicationSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed record UiApplicationSettings(
    string LibraryTheme,
    string ReaderTheme,
    string ReaderLayoutMode,
    string FontFamily,
    double FontSize,
    double LineHeight,
    double ParagraphSpacing,
    double HorizontalMargin,
    double ColumnWidth,
    string Alignment,
    string? SpeechVoice,
    int SpeechRate,
    bool HighlightSpokenSentence = true,
    bool FollowSpokenSentence = true,
    int SpeechVolume = 100,
    double SpeechPitch = 1,
    int PersonalReadingWordsPerMinute = 250)
{
    public static UiApplicationSettings Default { get; } =
        new(
            "Dark",
            "Midnight",
            "Scroll",
            "Georgia",
            20,
            1.55,
            14,
            48,
            760,
            "Start",
            null,
            175,
            HighlightSpokenSentence: true,
            FollowSpokenSentence: true,
            SpeechVolume: 100,
            SpeechPitch: 1,
            PersonalReadingWordsPerMinute: 250);
}

public interface IReaderGateway
{
    Task<ReaderDocument> OpenAsync(BookDescriptor book, CancellationToken cancellationToken);

    Task<ReaderDocument> OpenWithPasswordAsync(
        BookDescriptor book,
        string password,
        CancellationToken cancellationToken) =>
        OpenAsync(book, cancellationToken);
}

public sealed class ReaderPasswordRequiredException : Exception
{
    public ReaderPasswordRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface ITextToSpeechGateway
{
    IReadOnlyList<SpeechVoiceOption> Voices { get; }

    string ProviderStatus { get; }

    string PeterStatus { get; }

    Task RefreshVoicesAsync(CancellationToken cancellationToken);

    Task PreviewAsync(
        string voiceId,
        double rate,
        int volume,
        double pitch,
        CancellationToken cancellationToken);

    Task SpeakAsync(
        IReadOnlyList<string> segments,
        string voiceId,
        double rate,
        int volume,
        double pitch,
        int startIndex,
        CancellationToken cancellationToken);

    void Pause();

    void Resume();

    void Stop();

    void Previous()
    {
    }

    void Next()
    {
    }
}

public sealed record SpeechVoiceOption(
    string Id,
    string DisplayName,
    string Language,
    string Provider,
    string Architecture,
    bool IsDefault,
    bool SupportsRate,
    bool SupportsVolume,
    bool SupportsPitch)
{
    public override string ToString() => DisplayName;
}

public sealed record TextToSpeechProgress(
    int SegmentIndex,
    int SegmentCount,
    string Text);

public interface IProgressTextToSpeechGateway
{
    event EventHandler<TextToSpeechProgress>? ProgressChanged;
}

public interface IDiagnosticsGateway
{
    string CreateSnapshot();
}

public sealed record ReaderDocument(
    IReadOnlyList<ReaderBlockViewModel> Blocks,
    IReadOnlyList<TocItemViewModel> TableOfContents,
    bool IsFixedPage = false,
    int PageCount = 0,
    long EstimatedWordCount = 0);

public static class AppCompositionRoot
{
    public static AppServices CreatePreview()
    {
        var previewGateway = new LocalPreviewGateway();

        return new AppServices(
            previewGateway,
            previewGateway,
            new PreviewTextToSpeechGateway(),
            new DiagnosticsGateway(),
            registryCount: 8,
            isPreview: true);
    }

    public static async Task<AppServices> CreateProductionAsync(
        string? appDataRoot = null,
        CancellationToken cancellationToken = default)
    {
        var paths = new PlatformAppDataPaths(appDataRoot);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var pdfRenderer = new PdfPageRenderer();
        IBookFormatAdapter[] adapters =
        {
            new EpubFormatAdapter(),
            new PdfFormatAdapter(pdfRenderer),
            new MobiFormatAdapter(),
            new Azw3FormatAdapter(),
            new Fb2FormatAdapter(),
            new TxtFormatAdapter(),
            new RtfFormatAdapter(),
            new DocxFormatAdapter()
        };
        var registry = new AdapterBookFormatRegistry(adapters);
        var importer = new ManagedBookImportService(paths, repository, registry);
        var documents = new RegistryBookDocumentService(registry);
        var espeakEngine = new EspeakSpeechEngine(
            new EspeakDiscoveryOptions(
                BundleRoot: Path.Combine(
                    AppContext.BaseDirectory,
                    "tools",
                    "espeak-ng",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)));
        var speechEngine = new CompositeSpeechEngine(
            new ISpeechEngine[]
            {
                new WindowsSpeechEngine(),
                espeakEngine
            });
        await speechEngine.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var gateway = new ProductionLibraryGateway(
            repository,
            importer,
            documents,
            pdfRenderer);
        var speech = new ProductionTextToSpeechGateway(speechEngine);

        return new AppServices(
            gateway,
            gateway,
            speech,
            new DiagnosticsGateway(speechEngine.CreateSpeechDiagnostics),
            registry.Adapters.Count,
            isPreview: false,
            ownedServices: new IAsyncDisposable[]
            {
                repository,
                importer,
                speechEngine
            });
    }

    public static async Task<AppServices> CreateForApplicationAsync(
        CancellationToken cancellationToken = default)
    {
        var previewRequested = string.Equals(
            Environment.GetEnvironmentVariable("OPENSHELF_PREVIEW"),
            "1",
            StringComparison.Ordinal);
        return previewRequested
            ? CreatePreview()
            : await CreateProductionAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
    }
}

/// <summary>
/// Keeps the native UI independently runnable while Core/Infrastructure adapters are composed.
/// Replace only this gateway at the composition root when the production services are available.
/// It never scans until the user explicitly supplies a file or folder path.
/// </summary>
internal sealed class LocalPreviewGateway : ILibraryGateway, IReaderGateway
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".epub",
            ".pdf",
            ".mobi",
            ".azw3",
            ".fb2",
            ".txt",
            ".rtf",
            ".docx"
        };

    public async Task<UiImportBatchResult> ImportAsync(
        IReadOnlyList<string> selectedPaths,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
            () => ImportCore(selectedPaths, progress, cancellationToken),
            cancellationToken);
    }

    public Task<ReaderDocument> OpenAsync(
        BookDescriptor book,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var blocks = new List<ReaderBlockViewModel>
        {
            new ReaderHeadingBlockViewModel("start", book.Title),
            new ReaderParagraphBlockViewModel(
                "intro-1",
                $"This is the native reading surface for {book.Title}. Text is rendered with Avalonia controls, remains selectable, and does not use a browser or WebView."),
            new ReaderParagraphBlockViewModel(
                "intro-2",
                "OpenShelf keeps the page quiet and spacious. Select any passage to copy it, create a highlight, or attach a note. Your reading position and annotations stay attached when you change type size, theme, or page width.")
        };

        if (book.Cover is not null)
        {
            blocks.Add(
                new ReaderImageBlockViewModel(
                    "cover-image",
                    book.Cover,
                    $"Cover of {book.Title}",
                    "Images are supplied as decoded native image resources by the format adapter."));
        }

        blocks.Add(new ReaderHeadingBlockViewModel("chapter-1", "A calmer place to read"));
        blocks.Add(
            new ReaderParagraphBlockViewModel(
                "chapter-1-p1",
                "A good reader should disappear behind the book. OpenShelf uses a focused column, restrained controls, and a soft dark canvas so the content remains primary. Reader controls are always available from the keyboard, but move out of the way when they are not needed."));
        blocks.Add(
            new ReaderParagraphBlockViewModel(
                "chapter-1-p2",
                "The EPUB adapter can provide paragraphs, headings, and decoded images through the same document contract. Broken resources degrade to an accessible placeholder while the rest of the chapter remains readable."));
        blocks.Add(new ReaderHeadingBlockViewModel("chapter-2", "Make the page yours"));
        blocks.Add(
            new ReaderParagraphBlockViewModel(
                "chapter-2-p1",
                "Use the appearance panel to tune font size, line height, page width, margins, and reading theme. These settings update the native document surface without changing the source book."));

        var toc = new[]
        {
            new TocItemViewModel("Opening", "start", 1, 0),
            new TocItemViewModel("A calmer place to read", "chapter-1", 1, 0.36),
            new TocItemViewModel("Make the page yours", "chapter-2", 1, 0.76)
        };

        return Task.FromResult(new ReaderDocument(blocks, toc));
    }

    private static UiImportBatchResult ImportCore(
        IReadOnlyList<string> selectedPaths,
        IProgress<ImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var results = new List<ImportResultItemViewModel>();

        progress.Report(new ImportProgress("Scanning selected items…", 0, 0, 0, 0, 0, true));

        foreach (var selectedPath in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(selectedPath))
                {
                    candidates.Add(selectedPath);
                    continue;
                }

                if (Directory.Exists(selectedPath))
                {
                    candidates.AddRange(
                        Directory
                            .EnumerateFiles(selectedPath, "*", SearchOption.AllDirectories)
                            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))));
                    continue;
                }

                results.Add(
                    new ImportResultItemViewModel(
                        Path.GetFileName(selectedPath),
                        false,
                        "The selected item is no longer available."));
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                    or IOException
                    or DirectoryNotFoundException)
            {
                results.Add(
                    new ImportResultItemViewModel(
                        Path.GetFileName(selectedPath),
                        false,
                        exception.Message));
            }
        }

        var uniqueCandidates = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var imported = new List<BookDescriptor>();
        var skipped = 0;
        var failed = results.Count;

        for (var index = 0; index < uniqueCandidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = uniqueCandidates[index];
            var extension = Path.GetExtension(path);

            if (!SupportedExtensions.Contains(extension))
            {
                skipped++;
                results.Add(
                    new ImportResultItemViewModel(
                        Path.GetFileName(path),
                        false,
                        "Unsupported format."));
            }
            else
            {
                try
                {
                    var title = HumanizeTitle(Path.GetFileNameWithoutExtension(path));
                    var parent = Path.GetDirectoryName(path) ?? string.Empty;
                    var cover = TryLoadSidecarCover(path);

                    imported.Add(
                        new BookDescriptor(
                            Guid.NewGuid(),
                            title,
                            "Unknown author",
                            extension.TrimStart('.').ToUpperInvariant(),
                            path,
                            parent,
                            "Uncategorised",
                            cover));

                    results.Add(
                        new ImportResultItemViewModel(
                            Path.GetFileName(path),
                            true,
                            "Ready in your library."));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    results.Add(
                        new ImportResultItemViewModel(
                            Path.GetFileName(path),
                            false,
                            exception.Message));
                }
            }

            progress.Report(
                new ImportProgress(
                    Path.GetFileName(path),
                    index + 1,
                    uniqueCandidates.Length,
                    imported.Count,
                    skipped,
                    failed));
        }

        return new UiImportBatchResult(imported, results, skipped, false);
    }

    private static Bitmap? TryLoadSidecarCover(string bookPath)
    {
        var directory = Path.GetDirectoryName(bookPath);
        var stem = Path.GetFileNameWithoutExtension(bookPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
        {
            var coverPath = Path.Combine(directory, stem + extension);
            if (!File.Exists(coverPath))
            {
                continue;
            }

            try
            {
                return new Bitmap(coverPath);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string HumanizeTitle(string fileName)
    {
        var words = fileName
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 0 ? "Untitled" : string.Join(' ', words);
    }
}

internal sealed class PreviewTextToSpeechGateway : ITextToSpeechGateway
{
    public IReadOnlyList<SpeechVoiceOption> Voices { get; } = new[]
    {
        new SpeechVoiceOption(
            "preview:system",
            "System default",
            "en-GB",
            "Preview",
            "Current",
            true,
            true,
            true,
            true)
    };

    public string ProviderStatus => "Preview speech provider";

    public string PeterStatus => "Peter will appear when a licensed SAPI voice is available.";

    public Task RefreshVoicesAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PreviewAsync(
        string voiceId,
        double rate,
        int volume,
        double pitch,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SpeakAsync(
        IReadOnlyList<string> segments,
        string voiceId,
        double rate,
        int volume,
        double pitch,
        int startIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop()
    {
    }
}

internal sealed class DiagnosticsGateway : IDiagnosticsGateway
{
    private readonly Func<string>? _speechDiagnostics;

    public DiagnosticsGateway(Func<string>? speechDiagnostics = null)
    {
        _speechDiagnostics = speechDiagnostics;
    }

    public string CreateSnapshot()
    {
        var platform = string.Join(
            Environment.NewLine,
            $"OpenShelf Reader {typeof(DiagnosticsGateway).Assembly.GetName().Version}",
            $"OS: {RuntimeInformation.OSDescription}",
            $"Architecture: {RuntimeInformation.ProcessArchitecture}",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}",
            $"Captured: {DateTimeOffset.Now:O}",
            string.Empty);
        var speech = _speechDiagnostics?.Invoke();
        return string.Join(
            Environment.NewLine,
            platform,
            string.IsNullOrWhiteSpace(speech) ? "Speech: no provider diagnostics." : speech,
            string.Empty,
            "No book contents, titles, paths, notes, or reading history are included.");
    }
}
