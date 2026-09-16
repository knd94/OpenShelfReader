using System.Collections.Immutable;

namespace OpenShelf.Core;

public abstract record DocumentAnchor(Guid BookId, string RevisionHash);

public sealed record ReflowableAnchor(
    Guid BookId,
    string RevisionHash,
    string SectionId,
    string BlockId,
    int StartOffset,
    int EndOffset,
    string? ExactQuote = null,
    string? Prefix = null,
    string? Suffix = null)
    : DocumentAnchor(BookId, RevisionHash);

public sealed record PdfAnchor(
    Guid BookId,
    string RevisionHash,
    int PageIndex,
    int StartCharacter,
    int CharacterLength,
    ImmutableArray<DocumentRectangle> Rectangles,
    string? ExactQuote = null,
    double WithinPageOffset = 0)
    : DocumentAnchor(BookId, RevisionHash);

public sealed record DocumentRectangle(double X, double Y, double Width, double Height);

public enum AnnotationKind
{
    Bookmark,
    Highlight,
    Note
}

public enum HighlightColor
{
    Yellow,
    Green,
    Blue,
    Pink
}

public sealed record Annotation(
    Guid Id,
    Guid BookId,
    AnnotationKind Kind,
    DocumentAnchor Anchor,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    string? Text = null,
    string? Label = null,
    HighlightColor? Color = null);

public sealed record SpeechPosition(
    DocumentAnchor Anchor,
    int SentenceIndex,
    int CharacterOffset);

public sealed record ReadingPosition(
    Guid BookId,
    DocumentAnchor? Anchor,
    double Progress,
    string? ChapterLabel,
    int? PageNumber,
    int? PageCount,
    DateTimeOffset UpdatedAt,
    SpeechPosition? LastSpokenPosition = null)
{
    public double ClampedProgress => Math.Clamp(Progress, 0, 1);
}
