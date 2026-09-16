using System.Collections.Immutable;

namespace OpenShelf.Core;

public sealed record LibraryBook(
    Guid Id,
    string ManagedPath,
    BookFormat Format,
    string ContentHash,
    BookMetadata EmbeddedMetadata,
    string? TitleOverride,
    string? AuthorOverride,
    byte[]? CoverImage,
    string? CoverMediaType,
    bool IsFavorite,
    ReadingPosition? ReadingPosition,
    DateTimeOffset ImportedAt,
    DateTimeOffset? LastOpenedAt,
    ImmutableArray<Category> Categories,
    long EstimatedWordCount = 0)
{
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(TitleOverride) ? EmbeddedMetadata.Title : TitleOverride;

    public string DisplayAuthor =>
        string.IsNullOrWhiteSpace(AuthorOverride) ? EmbeddedMetadata.Author : AuthorOverride;

    public double Progress => ReadingPosition?.ClampedProgress ?? 0;
}

public sealed record Category(Guid Id, string Name, DateTimeOffset CreatedAt);

public enum LibraryTheme
{
    Dark,
    Light,
    System
}

public enum ReaderTheme
{
    Light,
    Sepia,
    Dark
}

public enum ReaderLayoutMode
{
    Scroll,
    Paged
}

public sealed record ReaderPreferences(
    ReaderTheme Theme,
    ReaderLayoutMode LayoutMode,
    string FontFamily,
    double FontSize,
    double LineHeight,
    double ParagraphSpacing,
    double HorizontalMargin,
    double ColumnWidth,
    TextAlignmentKind Alignment)
{
    public static ReaderPreferences Default { get; } = new(
        ReaderTheme.Dark,
        ReaderLayoutMode.Scroll,
        "Georgia",
        20,
        1.55,
        14,
        48,
        760,
        TextAlignmentKind.Start);
}

public sealed record ApplicationSettings(
    LibraryTheme LibraryTheme,
    ReaderPreferences Reader,
    string? SpeechVoice,
    int SpeechRate,
    bool ConfirmExternalLinks,
    bool HighlightSpokenSentence = true,
    bool FollowSpokenSentence = true,
    int SpeechVolume = 100,
    double SpeechPitch = 1,
    int PersonalReadingWordsPerMinute = 250)
{
    public static ApplicationSettings Default { get; } = new(
        LibraryTheme.Dark,
        ReaderPreferences.Default,
        null,
        175,
        true,
        HighlightSpokenSentence: true,
        FollowSpokenSentence: true,
        SpeechVolume: 100,
        SpeechPitch: 1,
        PersonalReadingWordsPerMinute: 250);
}

public enum LibrarySort
{
    Title,
    Author,
    RecentlyAdded,
    RecentlyOpened,
    Progress
}

public sealed record LibraryQuery(
    string? Search = null,
    Guid? CategoryId = null,
    bool FavoritesOnly = false,
    BookFormat? Format = null,
    LibrarySort Sort = LibrarySort.Title,
    bool Descending = false);
