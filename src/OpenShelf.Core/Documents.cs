using System.Collections.Immutable;

namespace OpenShelf.Core;

public enum BookFormat
{
    Epub,
    Pdf,
    Mobi,
    Azw3,
    Fb2,
    Txt,
    Rtf,
    Docx
}

public enum DocumentKind
{
    Reflowable,
    FixedPage
}

public sealed record BookMetadata(
    string Title,
    string Author,
    string? Description = null,
    string? Language = null,
    string? Publisher = null,
    string? Identifier = null);

public abstract record BookDocument(
    DocumentKind Kind,
    BookMetadata Metadata,
    string RevisionHash,
    long NormalizedLength);

public sealed record ReflowableDocument(
    BookMetadata Metadata,
    string RevisionHash,
    long NormalizedLength,
    ImmutableArray<DocumentSection> Sections,
    ImmutableArray<TableOfContentsItem> TableOfContents,
    ImmutableDictionary<string, BookResource> Resources)
    : BookDocument(DocumentKind.Reflowable, Metadata, RevisionHash, NormalizedLength);

public sealed record FixedPageDocument(
    BookMetadata Metadata,
    string RevisionHash,
    long NormalizedLength,
    ImmutableArray<FixedPage> Pages,
    ImmutableArray<TableOfContentsItem> TableOfContents)
    : BookDocument(DocumentKind.FixedPage, Metadata, RevisionHash, NormalizedLength);

public sealed record DocumentSection(
    string Id,
    string? Title,
    ImmutableArray<DocumentBlock> Blocks,
    long NormalizedStart,
    long NormalizedLength);

public sealed record TableOfContentsItem(
    string Id,
    string Title,
    string TargetSectionId,
    string? TargetBlockId,
    ImmutableArray<TableOfContentsItem> Children);

public abstract record DocumentBlock(string Id);

public sealed record HeadingBlock(
    string Id,
    int Level,
    ImmutableArray<InlineContent> Content) : DocumentBlock(Id);

public sealed record ParagraphBlock(
    string Id,
    ImmutableArray<InlineContent> Content,
    TextAlignmentKind Alignment = TextAlignmentKind.Start,
    bool IsPreformatted = false) : DocumentBlock(Id);

public sealed record QuoteBlock(
    string Id,
    ImmutableArray<DocumentBlock> Blocks,
    string? Attribution = null) : DocumentBlock(Id);

public sealed record ListBlock(
    string Id,
    bool IsOrdered,
    int Start,
    ImmutableArray<ListItemBlock> Items) : DocumentBlock(Id);

public sealed record ListItemBlock(ImmutableArray<DocumentBlock> Blocks);

public sealed record TableBlock(
    string Id,
    ImmutableArray<TableRow> Rows) : DocumentBlock(Id);

public sealed record TableRow(ImmutableArray<TableCell> Cells);

public sealed record TableCell(
    ImmutableArray<DocumentBlock> Blocks,
    int ColumnSpan = 1,
    int RowSpan = 1,
    bool IsHeader = false);

public sealed record ImageBlock(
    string Id,
    string ResourceId,
    string? AlternativeText,
    double? IntrinsicWidth,
    double? IntrinsicHeight,
    string? Caption = null) : DocumentBlock(Id);

public sealed record PageBreakBlock(string Id, string? Label = null) : DocumentBlock(Id);

public sealed record HorizontalRuleBlock(string Id) : DocumentBlock(Id);

public abstract record InlineContent;

public sealed record TextRun(
    string Text,
    TextStyle Style = TextStyle.None,
    string? Foreground = null,
    string? Background = null,
    double? RelativeSize = null) : InlineContent;

public sealed record InlineImage(
    string ResourceId,
    string? AlternativeText,
    double? Width,
    double? Height) : InlineContent;

public sealed record LineBreak : InlineContent;

public sealed record HyperlinkStart(string Target, bool IsExternal) : InlineContent;

public sealed record HyperlinkEnd : InlineContent;

[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
    Superscript = 16,
    Subscript = 32,
    Code = 64
}

public enum TextAlignmentKind
{
    Start,
    Center,
    End,
    Justify
}

public sealed record BookResource(
    string Id,
    string MediaType,
    byte[] Data,
    string? OriginalPath = null);

public sealed record FixedPage(
    int Index,
    double Width,
    double Height,
    string PlainText,
    ImmutableArray<TextGlyph> Glyphs,
    string? RenderCacheKey = null);

public sealed record TextGlyph(
    int CharacterIndex,
    string Text,
    double Left,
    double Top,
    double Right,
    double Bottom);

public static class DocumentText
{
    public static string Flatten(IEnumerable<InlineContent> content)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var inline in content)
        {
            switch (inline)
            {
                case TextRun run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.AppendLine();
                    break;
                case InlineImage image when !string.IsNullOrWhiteSpace(image.AlternativeText):
                    builder.Append(image.AlternativeText);
                    break;
            }
        }

        return builder.ToString();
    }
}
