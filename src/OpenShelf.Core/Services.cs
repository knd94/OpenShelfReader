using System.Collections.Immutable;

namespace OpenShelf.Core;

public interface ILibraryRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryBook>> QueryBooksAsync(
        LibraryQuery query,
        CancellationToken cancellationToken = default);
    Task<LibraryBook?> GetBookAsync(Guid bookId, CancellationToken cancellationToken = default);
    Task<LibraryBook?> FindByHashAsync(string contentHash, CancellationToken cancellationToken = default);
    Task AddBookAsync(LibraryBook book, CancellationToken cancellationToken = default);
    Task RemoveBookAsync(Guid bookId, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(
        Guid bookId,
        string? titleOverride,
        string? authorOverride,
        CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(Guid bookId, bool isFavorite, CancellationToken cancellationToken = default);
    Task SaveEstimatedWordCountAsync(
        Guid bookId,
        long estimatedWordCount,
        CancellationToken cancellationToken = default);
    Task SaveReadingPositionAsync(
        ReadingPosition position,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<Category> CreateCategoryAsync(string name, CancellationToken cancellationToken = default);
    Task RenameCategoryAsync(Guid categoryId, string name, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task SetBookCategoriesAsync(
        Guid bookId,
        IEnumerable<Guid> categoryIds,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Annotation>> GetAnnotationsAsync(
        Guid bookId,
        CancellationToken cancellationToken = default);
    Task SaveAnnotationAsync(Annotation annotation, CancellationToken cancellationToken = default);
    Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default);
    Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
}

public interface IBookDocumentService
{
    Task<BookDocument> OpenAsync(
        LibraryBook book,
        string? password,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken);
}

public enum SpeechState
{
    Stopped,
    Speaking,
    Paused,
    Unavailable
}

public enum SpeechProvider
{
    WindowsModern,
    WindowsSapi,
    BuiltIn
}

public enum SpeechArchitecture
{
    Current,
    X64,
    X86
}

public sealed record SpeechVoice(
    string Id,
    string DisplayName,
    string Language,
    SpeechProvider Provider = SpeechProvider.BuiltIn,
    SpeechArchitecture Architecture = SpeechArchitecture.Current,
    bool IsAvailable = true,
    string? UnavailableReason = null,
    bool SupportsRate = true,
    bool SupportsVolume = true,
    bool SupportsPitch = true,
    bool IsDefault = false);

public sealed record SpeechOptions(
    string? VoiceId,
    int WordsPerMinute,
    int Volume,
    double Pitch)
{
    public static SpeechOptions Default { get; } = new(
        VoiceId: null,
        WordsPerMinute: 175,
        Volume: 100,
        Pitch: 1);

    public SpeechOptions Clamped => this with
    {
        WordsPerMinute = Math.Clamp(WordsPerMinute, 80, 450),
        Volume = Math.Clamp(Volume, 0, 100),
        Pitch = Math.Clamp(Pitch, 0.5, 2)
    };
}

public sealed record SpeechProgress(
    int SegmentIndex,
    int SegmentCount,
    string Text,
    int CharacterOffset,
    int CharacterLength);

public interface ISpeechEngine : IAsyncDisposable
{
    SpeechState State { get; }
    ImmutableArray<SpeechVoice> Voices { get; }
    event EventHandler<SpeechProgress>? ProgressChanged;
    event EventHandler<SpeechState>? StateChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshVoicesAsync(CancellationToken cancellationToken = default);
    Task SpeakAsync(
        IReadOnlyList<string> sentenceSegments,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken);
    Task PreviewAsync(
        SpeechOptions options,
        string sampleText,
        CancellationToken cancellationToken);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task PreviousAsync();
    Task NextAsync();
}

public interface ISpeechDiagnostics
{
    string CreateSpeechDiagnostics();
}

public static class SupportedBookFormats
{
    public static ImmutableDictionary<string, BookFormat> Extensions { get; } =
        new Dictionary<string, BookFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".epub"] = BookFormat.Epub,
            [".pdf"] = BookFormat.Pdf,
            [".mobi"] = BookFormat.Mobi,
            [".azw3"] = BookFormat.Azw3,
            [".fb2"] = BookFormat.Fb2,
            [".txt"] = BookFormat.Txt,
            [".rtf"] = BookFormat.Rtf,
            [".docx"] = BookFormat.Docx
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
}
