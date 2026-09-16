using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;

namespace OpenShelf.App.ViewModels;

public sealed class BookItemViewModel : ViewModelBase
{
    private string _title;
    private string _author;
    private bool _isFavorite;
    private double _progress;
    private long _estimatedWordCount;
    private int _personalReadingWordsPerMinute = 250;
    private int _speechWordsPerMinute = 175;

    public BookItemViewModel(BookDescriptor descriptor)
    {
        Id = descriptor.Id;
        _title = descriptor.Title;
        _author = descriptor.Author;
        Format = descriptor.Format;
        SourcePath = descriptor.SourcePath;
        SourceFolder = descriptor.SourceFolder;
        Category = descriptor.Category;
        Cover = descriptor.Cover;
        _progress = Math.Clamp(descriptor.Progress, 0, 1);
        _estimatedWordCount = Math.Max(0, descriptor.EstimatedWordCount);
        _isFavorite = descriptor.IsFavorite;
        ReadingLocator = descriptor.ReadingLocator;
        SpeechLocator = descriptor.SpeechLocator;
        SpeechSentenceIndex = Math.Max(0, descriptor.SpeechSentenceIndex);
        SpeechCharacterOffset = Math.Max(0, descriptor.SpeechCharacterOffset);
        ImportedAt = descriptor.ImportedAt;
        FallbackCover = CoverPalette.Create(descriptor.Title);
    }

    public Guid Id { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Author
    {
        get => _author;
        set => SetProperty(ref _author, value);
    }

    public string Format { get; }

    public string SourcePath { get; }

    public string SourceFolder { get; }

    public string Category { get; set; }

    public string ReadingLocator { get; set; }

    public string SpeechLocator { get; set; }

    public int SpeechSentenceIndex { get; set; }

    public int SpeechCharacterOffset { get; set; }

    public DateTimeOffset ImportedAt { get; }

    public IImage? Cover { get; }

    public IBrush FallbackCover { get; }

    public bool HasCover => Cover is not null;

    public bool UsesFallbackCover => Cover is null;

    public string CoverInitial =>
        string.IsNullOrWhiteSpace(Title) ? "O" : Title.Trim()[0].ToString().ToUpperInvariant();

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
                OnPropertyChanged(nameof(FavoriteLabel));
            }
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public string FavoriteLabel => IsFavorite ? "Remove from favourites" : "Add to favourites";

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressLabel));
                OnPropertyChanged(nameof(TimeRemainingLabel));
                OnPropertyChanged(nameof(ProgressAndTimeLabel));
            }
        }
    }

    public double ProgressPercent => Progress * 100;

    public string ProgressLabel =>
        Progress <= 0
            ? "Unread"
            : Progress >= 1
                ? "Finished"
                : $"{Math.Clamp((int)Math.Round(Progress * 100), 1, 99)}%";

    public long EstimatedWordCount
    {
        get => _estimatedWordCount;
        set
        {
            if (SetProperty(ref _estimatedWordCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(TimeRemainingLabel));
                OnPropertyChanged(nameof(ProgressAndTimeLabel));
            }
        }
    }

    public string TimeRemainingLabel
    {
        get
        {
            if (Progress >= 1)
            {
                return "0 min left";
            }

            if (EstimatedWordCount <= 0)
            {
                return "Open to estimate time";
            }

            var remainingWords = EstimatedWordCount * (1 - Progress);
            return $"Read {FormatDuration(remainingWords, _personalReadingWordsPerMinute)} · TTS {FormatDuration(remainingWords, _speechWordsPerMinute)}";
        }
    }

    public string ProgressAndTimeLabel => $"{ProgressLabel} · {TimeRemainingLabel}";

    public void SetTimeEstimateSpeeds(
        int personalReadingWordsPerMinute,
        int speechWordsPerMinute)
    {
        var personal = Math.Clamp(personalReadingWordsPerMinute, 100, 1000);
        var speech = Math.Clamp(speechWordsPerMinute, 80, 450);
        if (_personalReadingWordsPerMinute == personal
            && _speechWordsPerMinute == speech)
        {
            return;
        }

        _personalReadingWordsPerMinute = personal;
        _speechWordsPerMinute = speech;
        OnPropertyChanged(nameof(TimeRemainingLabel));
        OnPropertyChanged(nameof(ProgressAndTimeLabel));
    }

    private static string FormatDuration(double words, int wordsPerMinute)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(words / wordsPerMinute));
        if (minutes < 60)
        {
            return $"{minutes}m";
        }

        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
    }
}

public sealed record BookDescriptor(
    Guid Id,
    string Title,
    string Author,
    string Format,
    string SourcePath,
    string SourceFolder,
    string Category,
    IImage? Cover,
    double Progress = 0,
    bool IsFavorite = false,
    string ReadingLocator = "",
    DateTimeOffset ImportedAt = default,
    string SpeechLocator = "",
    int SpeechSentenceIndex = 0,
    int SpeechCharacterOffset = 0,
    long EstimatedWordCount = 0);

public sealed class CategoryItemViewModel
{
    public CategoryItemViewModel(string name, int count = 0)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    public int Count { get; }

    public string CountLabel => Count.ToString();
}

public abstract class ReaderBlockViewModel : ViewModelBase
{
    protected ReaderBlockViewModel(string locator, long normalizedLength = 1)
    {
        Locator = locator;
        NormalizedLength = Math.Max(1, normalizedLength);
    }

    public string Locator { get; }

    public long NormalizedLength { get; }
}

public sealed record ReaderHighlightRange(
    int Start,
    int Length,
    string ColorName);

/// <summary>
/// A temporary, playback-only range. It is deliberately separate from
/// ReaderHighlightRange so spoken tracking can never be saved as an annotation.
/// </summary>
public sealed record ReaderSpokenRange(int Start, int Length);

public abstract class ReaderTextBlockViewModel : ReaderBlockViewModel
{
    private IReadOnlyList<ReaderHighlightRange> _highlightRanges =
        Array.Empty<ReaderHighlightRange>();
    private ReaderSpokenRange? _spokenRange;

    protected ReaderTextBlockViewModel(
        string locator,
        string text,
        int sourceOffset = 0)
        : base(locator, text?.Length ?? 0)
    {
        Text = text ?? string.Empty;
        SourceOffset = Math.Max(0, sourceOffset);
    }

    public string Text { get; }

    /// <summary>
    /// Character offset in the original document block. Paged reader fragments
    /// retain this offset so selections, annotations, saved positions, and TTS
    /// tracking continue to use stable document anchors after reflow.
    /// </summary>
    public int SourceOffset { get; }

    public IReadOnlyList<ReaderHighlightRange> HighlightRanges
    {
        get => _highlightRanges;
        private set => SetProperty(ref _highlightRanges, value);
    }

    public ReaderSpokenRange? SpokenRange
    {
        get => _spokenRange;
        private set => SetProperty(ref _spokenRange, value);
    }

    public void SetHighlightRanges(IEnumerable<ReaderHighlightRange> ranges)
    {
        HighlightRanges = new List<ReaderHighlightRange>(ranges);
    }

    public void SetSpokenRange(ReaderSpokenRange? range)
    {
        SpokenRange = range;
    }
}

public sealed class ReaderHeadingBlockViewModel : ReaderTextBlockViewModel
{
    private double _fontSize = 32;

    public ReaderHeadingBlockViewModel(
        string locator,
        string text,
        int sourceOffset = 0)
        : base(locator, text, sourceOffset)
    {
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }
}

public sealed class ReaderParagraphBlockViewModel : ReaderTextBlockViewModel
{
    private double _fontSize = 19;
    private double _lineHeight = 31;
    private Thickness _paragraphMargin = new(0, 0, 0, 19);
    private TextAlignment _textAlignment = TextAlignment.Left;

    public ReaderParagraphBlockViewModel(
        string locator,
        string text,
        int sourceOffset = 0)
        : base(locator, text, sourceOffset)
    {
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }

    public double LineHeight
    {
        get => _lineHeight;
        set => SetProperty(ref _lineHeight, value);
    }

    public Thickness ParagraphMargin
    {
        get => _paragraphMargin;
        set => SetProperty(ref _paragraphMargin, value);
    }

    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set => SetProperty(ref _textAlignment, value);
    }
}

public sealed class ReaderPageViewModel
{
    public ReaderPageViewModel(
        int pageNumber,
        int pageCount,
        IReadOnlyList<ReaderBlockViewModel> blocks)
    {
        PageNumber = Math.Max(1, pageNumber);
        PageCount = Math.Max(PageNumber, pageCount);
        Blocks = blocks;
    }

    public int PageNumber { get; }

    public int PageCount { get; }

    public IReadOnlyList<ReaderBlockViewModel> Blocks { get; }

    public string AccessibleName => $"Reading page {PageNumber} of {PageCount}";
}

public sealed class ReaderImageBlockViewModel : ReaderBlockViewModel
{
    private readonly Func<CancellationToken, Task<IImage?>>? _imageLoader;
    private IImage? _image;
    private bool _isLoading;
    private bool _loadAttempted;

    public ReaderImageBlockViewModel(
        string locator,
        IImage? image,
        string altText,
        string? caption = null)
        : base(locator)
    {
        _image = image;
        _loadAttempted = image is not null;
        AltText = altText;
        Caption = caption ?? string.Empty;
    }

    public ReaderImageBlockViewModel(
        string locator,
        Func<CancellationToken, Task<IImage?>> imageLoader,
        string altText,
        string? caption = null)
        : base(locator)
    {
        _imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
        AltText = altText;
        Caption = caption ?? string.Empty;
    }

    public IImage? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(IsImageMissing));
            }
        }
    }

    public bool HasImage => Image is not null;

    public bool IsImageMissing => _loadAttempted && !_isLoading && Image is null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsImageMissing));
            }
        }
    }

    public string AltText { get; }

    public string Caption { get; }

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loadAttempted || _imageLoader is null)
        {
            return;
        }

        _loadAttempted = true;
        IsLoading = true;
        try
        {
            Image = await _imageLoader(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _loadAttempted = false;
            throw;
        }
        catch
        {
            Image = null;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed class ReaderNoticeBlockViewModel : ReaderBlockViewModel
{
    public ReaderNoticeBlockViewModel(string locator, string text)
        : base(locator, text?.Length ?? 0)
    {
        Text = text ?? string.Empty;
    }

    public string Text { get; }
}

public sealed record TocItemViewModel(string Title, string Locator, int Level, double Progress)
{
    public double LeftIndent => Math.Max(0, Level - 1) * 14;

    public Thickness IndentMargin => new(LeftIndent, 0, 0, 2);
}

public enum AnnotationKind
{
    Highlight,
    Note,
    Bookmark
}

public sealed class AnnotationItemViewModel
{
    public AnnotationItemViewModel(
        Guid id,
        AnnotationKind kind,
        string locator,
        string title,
        string excerpt,
        string note,
        DateTimeOffset createdAt,
        int anchorStart = 0,
        int anchorLength = 0,
        string highlightColorName = "Yellow")
    {
        Id = id;
        Kind = kind;
        Locator = locator;
        Title = title;
        Excerpt = excerpt;
        Note = note;
        CreatedAt = createdAt;
        AnchorStart = Math.Max(0, anchorStart);
        AnchorLength = Math.Max(0, anchorLength);
        HighlightColorName = highlightColorName;
    }

    public Guid Id { get; }

    public AnnotationKind Kind { get; }

    public string Locator { get; }

    public string Title { get; }

    public string Excerpt { get; }

    public string Note { get; }

    public DateTimeOffset CreatedAt { get; }

    public int AnchorStart { get; }

    public int AnchorLength { get; }

    public string HighlightColorName { get; }

    public string Glyph => Kind switch
    {
        AnnotationKind.Highlight => "▰",
        AnnotationKind.Note => "✎",
        AnnotationKind.Bookmark => "◆",
        _ => "•"
    };

    public string Timestamp => CreatedAt.LocalDateTime.ToString("g");

    public bool IsNote => Kind == AnnotationKind.Note;
}

public sealed record ReaderNavigationRequest(string Locator, double Progress);

public sealed record ImportProgress(
    string CurrentItem,
    int Completed,
    int Total,
    int Imported,
    int Skipped,
    int Failed,
    bool IsScanning = false);

public sealed record ImportResultItemViewModel(
    string Name,
    bool Succeeded,
    string Message,
    string? PasswordRetryToken = null)
{
    public string Glyph => Succeeded ? "✓" : "!";

    public string StatusBrushKey => Succeeded ? "OpenShelfSuccess" : "OpenShelfDanger";

    public bool RequiresPassword => !string.IsNullOrWhiteSpace(PasswordRetryToken);
}

public sealed record ImportBatchResult(
    IReadOnlyList<BookDescriptor> ImportedBooks,
    IReadOnlyList<ImportResultItemViewModel> Results,
    int Skipped,
    bool WasCancelled);

internal static class CoverPalette
{
    public static IBrush Create(string seed)
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Color.Parse("#E26F62"), 0),
                new(Color.Parse("#7A1938"), 1)
            }
        };
    }
}
