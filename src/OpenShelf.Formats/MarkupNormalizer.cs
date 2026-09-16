using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using OpenShelf.Core;

namespace OpenShelf.Formats;

internal sealed record ResolvedImage(
    string ResourceId,
    string? AlternativeText = null,
    double? Width = null,
    double? Height = null);

internal sealed record NormalizedMarkup(
    ImmutableArray<DocumentBlock> Blocks,
    ImmutableDictionary<string, string> AnchorToBlockId,
    string? FirstHeading);

internal sealed class MarkupNormalizer
{
    private static readonly HashSet<string> BlockElements =
    [
        "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DIV", "DL", "FIGURE",
        "FOOTER", "FORM", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER",
        "HR", "LI", "MAIN", "NAV", "OL", "P", "PRE", "SECTION", "TABLE", "UL"
    ];

    private readonly string sourceKey;
    private readonly Func<string, string?, ResolvedImage?> resolveImage;
    private readonly Func<string, string, string?, ResolvedImage?> resolveImageFromBase;
    private readonly bool includeCssBackgroundImages;
    private readonly ImmutableArray<CssBackgroundRule> cssBackgroundRules;
    private readonly Dictionary<string, string> anchors = new(StringComparer.Ordinal);
    private int blockIndex;
    private string? firstHeading;

    private MarkupNormalizer(
        string sourceKey,
        Func<string, string?, ResolvedImage?> resolveImage,
        bool includeCssBackgroundImages,
        IEnumerable<CssBackgroundRule>? cssBackgroundRules,
        Func<string, string, string?, ResolvedImage?>? resolveImageFromBase)
    {
        this.sourceKey = sourceKey;
        this.resolveImage = resolveImage;
        this.resolveImageFromBase = resolveImageFromBase ??
            ((_, reference, alternativeText) =>
                resolveImage(reference, alternativeText));
        this.includeCssBackgroundImages = includeCssBackgroundImages;
        this.cssBackgroundRules = cssBackgroundRules?.ToImmutableArray() ?? [];
    }

    public static NormalizedMarkup Normalize(
        string markup,
        string sourceKey,
        Func<string, string?, ResolvedImage?> resolveImage,
        bool includeCssBackgroundImages = false,
        IEnumerable<CssBackgroundRule>? cssBackgroundRules = null,
        Func<string, string, string?, ResolvedImage?>? resolveImageFromBase = null)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        var document = parser.ParseDocument(markup);
        var normalizer = new MarkupNormalizer(
            sourceKey,
            resolveImage,
            includeCssBackgroundImages,
            cssBackgroundRules,
            resolveImageFromBase);
        var blocks = new List<DocumentBlock>();
        normalizer.AppendContainer(
            document.Body ?? document.DocumentElement,
            blocks,
            includeOwnBackground: true);
        if (blocks.Count == 0)
        {
            var text = FormatUtilities.CleanText(document.DocumentElement?.TextContent);
            if (text.Length > 0)
            {
                blocks.Add(new ParagraphBlock(
                    normalizer.NextId("paragraph", document.DocumentElement),
                    [new TextRun(text)]));
            }
        }

        return new NormalizedMarkup(
            blocks.ToImmutableArray(),
            normalizer.anchors.ToImmutableDictionary(StringComparer.Ordinal),
            normalizer.firstHeading);
    }

    private void AppendContainer(
        INode? container,
        List<DocumentBlock> blocks,
        bool includeOwnBackground = false)
    {
        if (container is null)
        {
            return;
        }

        if (includeOwnBackground && container is IElement containerElement)
        {
            AppendCssBackgroundBlock(containerElement, blocks);
        }

        var pendingInline = new List<InlineContent>();
        void FlushInline()
        {
            TrimInlines(pendingInline);
            if (HasReadableContent(pendingInline))
            {
                blocks.Add(new ParagraphBlock(
                    NextId("paragraph", container as IElement),
                    pendingInline.ToImmutableArray()));
            }

            pendingInline.Clear();
        }

        foreach (var child in container.ChildNodes)
        {
            if (child is IElement element && BlockElements.Contains(element.TagName))
            {
                FlushInline();
                AppendBlock(element, blocks);
            }
            else
            {
                AppendInlines(child, TextStyle.None, pendingInline, preformatted: false);
            }
        }

        FlushInline();
    }

    private void AppendBlock(IElement element, List<DocumentBlock> blocks)
    {
        if (ShouldSkip(element))
        {
            return;
        }

        AppendCssBackgroundBlock(element, blocks);

        switch (element.TagName)
        {
            case "H1":
            case "H2":
            case "H3":
            case "H4":
            case "H5":
            case "H6":
            {
                var content = BuildInlines(element, preformatted: false);
                if (!HasReadableContent(content))
                {
                    return;
                }

                var id = NextId("heading", element);
                var title = FormatUtilities.CleanText(DocumentText.Flatten(content));
                firstHeading ??= title;
                blocks.Add(new HeadingBlock(id, element.TagName[1] - '0', content));
                return;
            }
            case "P":
            case "ADDRESS":
            {
                AddParagraph(element, blocks, preformatted: false);
                return;
            }
            case "PRE":
            {
                AddParagraph(element, blocks, preformatted: true);
                return;
            }
            case "BLOCKQUOTE":
            {
                var children = new List<DocumentBlock>();
                AppendContainer(element, children);
                if (children.Count > 0)
                {
                    blocks.Add(new QuoteBlock(
                        NextId("quote", element),
                        children.ToImmutableArray(),
                        element.GetAttribute("cite")));
                }

                return;
            }
            case "UL":
            case "OL":
            {
                var items = element.Children
                    .Where(child => child.TagName == "LI")
                    .Select(child =>
                    {
                        var itemBlocks = new List<DocumentBlock>();
                        AppendContainer(child, itemBlocks, includeOwnBackground: true);
                        if (itemBlocks.Count == 0)
                        {
                            AddParagraph(child, itemBlocks, preformatted: false);
                        }

                        return new ListItemBlock(itemBlocks.ToImmutableArray());
                    })
                    .Where(item => item.Blocks.Length > 0)
                    .ToImmutableArray();
                if (items.Length > 0)
                {
                    blocks.Add(new ListBlock(
                        NextId("list", element),
                        element.TagName == "OL",
                        ParsePositiveInt(element.GetAttribute("start")) ?? 1,
                        items));
                }

                return;
            }
            case "TABLE":
            {
                var rows = element
                    .QuerySelectorAll("tr")
                    .Select(row => new TableRow(
                        row.Children
                            .Where(cell => cell.TagName is "TD" or "TH")
                            .Select(cell =>
                            {
                                var cellBlocks = new List<DocumentBlock>();
                                AppendContainer(cell, cellBlocks, includeOwnBackground: true);
                                if (cellBlocks.Count == 0)
                                {
                                    AddParagraph(cell, cellBlocks, preformatted: false);
                                }

                                return new TableCell(
                                    cellBlocks.ToImmutableArray(),
                                    ParsePositiveInt(cell.GetAttribute("colspan")) ?? 1,
                                    ParsePositiveInt(cell.GetAttribute("rowspan")) ?? 1,
                                    cell.TagName == "TH");
                            })
                            .ToImmutableArray()))
                    .Where(row => row.Cells.Length > 0)
                    .ToImmutableArray();
                if (rows.Length > 0)
                {
                    blocks.Add(new TableBlock(NextId("table", element), rows));
                }

                return;
            }
            case "HR":
                blocks.Add(new HorizontalRuleBlock(NextId("rule", element)));
                return;
            case "FIGURE":
            {
                var image = element.QuerySelector("img");
                if (image is not null && TryCreateImageBlock(
                        image,
                        element.QuerySelector("figcaption")?.TextContent,
                        out var imageBlock))
                {
                    blocks.Add(imageBlock);
                    return;
                }

                AppendContainer(element, blocks);
                return;
            }
            case "DIV":
            case "SECTION":
            case "ARTICLE":
            case "MAIN":
            case "ASIDE":
            case "HEADER":
            case "FOOTER":
            case "NAV":
            case "FORM":
            case "DL":
                AppendContainer(element, blocks);
                return;
            default:
                if (element.TagName == "IMG" &&
                    TryCreateImageBlock(element, null, out var block))
                {
                    blocks.Add(block);
                    return;
                }

                AddParagraph(element, blocks, preformatted: false);
                return;
        }
    }

    private void AddParagraph(
        IElement element,
        List<DocumentBlock> blocks,
        bool preformatted)
    {
        var content = BuildInlines(element, preformatted);
        if (!HasReadableContent(content))
        {
            return;
        }

        blocks.Add(new ParagraphBlock(
            NextId(preformatted ? "pre" : "paragraph", element),
            content,
            ParseAlignment(element),
            preformatted));
    }

    private ImmutableArray<InlineContent> BuildInlines(
        IElement element,
        bool preformatted)
    {
        var inlines = new List<InlineContent>();
        foreach (var node in element.ChildNodes)
        {
            AppendInlines(node, TextStyle.None, inlines, preformatted);
        }

        if (!preformatted)
        {
            TrimInlines(inlines);
        }

        return inlines.ToImmutableArray();
    }

    private void AppendInlines(
        INode node,
        TextStyle inheritedStyle,
        List<InlineContent> output,
        bool preformatted)
    {
        if (node is IText textNode)
        {
            var text = preformatted
                ? textNode.Data.Replace("\r\n", "\n").Replace('\r', '\n')
                : CollapseWhitespace(textNode.Data);
            AppendText(output, text, inheritedStyle);
            return;
        }

        if (node is not IElement element || ShouldSkip(element))
        {
            return;
        }

        var style = ApplyStyle(element, inheritedStyle);
        if (element.TagName == "BR")
        {
            AppendCssBackgroundInline(element, style, output);
            output.Add(new LineBreak());
            return;
        }

        if (element.TagName == "IMG")
        {
            AppendCssBackgroundInline(element, style, output);
            var source = element.GetAttribute("src") ??
                element.GetAttribute("href") ??
                element.GetAttribute("recindex") ??
                element.GetAttribute("mediarecindex");
            if (!string.IsNullOrWhiteSpace(source))
            {
                var resolved = resolveImage(source, element.GetAttribute("alt"));
                if (resolved is not null)
                {
                    output.Add(new InlineImage(
                        resolved.ResourceId,
                        resolved.AlternativeText ?? element.GetAttribute("alt"),
                        resolved.Width ?? ParseDimension(element.GetAttribute("width")),
                        resolved.Height ?? ParseDimension(element.GetAttribute("height"))));
                }
                else
                {
                    var alternativeText = FormatUtilities.CleanText(
                        element.GetAttribute("alt"));
                    AppendText(
                        output,
                        alternativeText.Length > 0
                            ? alternativeText
                            : "[Image unavailable]",
                        inheritedStyle);
                }
            }

            return;
        }

        var isLink = element.TagName == "A";
        if (isLink)
        {
            var target = element.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(target))
            {
                output.Add(new HyperlinkStart(
                    target,
                    Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" or "mailto"));
            }
            else
            {
                isLink = false;
            }
        }

        AppendCssBackgroundInline(element, style, output);
        foreach (var child in element.ChildNodes)
        {
            AppendInlines(child, style, output, preformatted);
        }

        if (isLink)
        {
            output.Add(new HyperlinkEnd());
        }

        if (BlockElements.Contains(element.TagName) &&
            output.Count > 0 &&
            output[^1] is not LineBreak)
        {
            output.Add(new LineBreak());
        }
    }

    private void AppendCssBackgroundBlock(
        IElement element,
        List<DocumentBlock> blocks)
    {
        if (!TryGetCssBackgroundImage(element, out var basePath, out var reference))
        {
            return;
        }

        var alternativeText = GetBackgroundAlternativeText(element);
        var resolved = resolveImageFromBase(basePath, reference, alternativeText);
        if (resolved is null)
        {
            blocks.Add(new ParagraphBlock(
                NextId("image-placeholder", element),
                [new TextRun(alternativeText ?? "[Image unavailable]")]));
            return;
        }

        blocks.Add(new ImageBlock(
            NextId("background-image", element),
            resolved.ResourceId,
            resolved.AlternativeText ?? alternativeText,
            resolved.Width ?? ParseDimension(element.GetAttribute("width")),
            resolved.Height ?? ParseDimension(element.GetAttribute("height"))));
    }

    private void AppendCssBackgroundInline(
        IElement element,
        TextStyle style,
        List<InlineContent> output)
    {
        if (!TryGetCssBackgroundImage(element, out var basePath, out var reference))
        {
            return;
        }

        var alternativeText = GetBackgroundAlternativeText(element);
        var resolved = resolveImageFromBase(basePath, reference, alternativeText);
        if (resolved is null)
        {
            AppendText(
                output,
                (alternativeText ?? "[Image unavailable]") + " ",
                style);
            return;
        }

        output.Add(new InlineImage(
            resolved.ResourceId,
            resolved.AlternativeText ?? alternativeText,
            resolved.Width ?? ParseDimension(element.GetAttribute("width")),
            resolved.Height ?? ParseDimension(element.GetAttribute("height"))));
    }

    private bool TryGetCssBackgroundImage(
        IElement element,
        out string basePath,
        out string reference)
    {
        basePath = sourceKey;
        reference = string.Empty;
        if (!includeCssBackgroundImages)
        {
            return false;
        }

        if (InlineCssImageParser.TryExtractBackgroundImage(
                element.GetAttribute("style"),
                out reference))
        {
            return true;
        }

        CssBackgroundRule? selected = null;
        foreach (var rule in cssBackgroundRules)
        {
            if (!rule.Matches(element) ||
                selected is not null &&
                (rule.Specificity < selected.Specificity ||
                 rule.Specificity == selected.Specificity &&
                 rule.SourceOrder < selected.SourceOrder))
            {
                continue;
            }

            selected = rule;
        }

        if (selected is null)
        {
            return false;
        }

        basePath = selected.BasePath;
        reference = selected.ImageReference;
        return true;
    }

    private static string? GetBackgroundAlternativeText(IElement element)
    {
        foreach (var value in new[]
                 {
                     element.GetAttribute("aria-label"),
                     element.GetAttribute("title"),
                     element.GetAttribute("alt")
                 })
        {
            var cleaned = FormatUtilities.CleanText(value);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return null;
    }

    private bool TryCreateImageBlock(
        IElement image,
        string? caption,
        out ImageBlock block)
    {
        block = null!;
        var source = image.GetAttribute("src") ??
            image.GetAttribute("href") ??
            image.GetAttribute("recindex") ??
            image.GetAttribute("mediarecindex");
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var resolved = resolveImage(source, image.GetAttribute("alt"));
        if (resolved is null)
        {
            return false;
        }

        block = new ImageBlock(
            NextId("image", image),
            resolved.ResourceId,
            resolved.AlternativeText ?? image.GetAttribute("alt"),
            resolved.Width ?? ParseDimension(image.GetAttribute("width")),
            resolved.Height ?? ParseDimension(image.GetAttribute("height")),
            FormatUtilities.CleanText(caption) is { Length: > 0 } cleanCaption
                ? cleanCaption
                : null);
        return true;
    }

    private string NextId(string type, IElement? element)
    {
        var id = FormatUtilities.StableId(
            type,
            sourceKey,
            (blockIndex++).ToString(CultureInfo.InvariantCulture),
            element?.Id);
        if (!string.IsNullOrWhiteSpace(element?.Id))
        {
            anchors.TryAdd(element.Id, id);
        }

        return id;
    }

    private static void AppendText(
        List<InlineContent> output,
        string text,
        TextStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (output.Count > 0 && output[^1] is TextRun previous && previous.Style == style)
        {
            output[^1] = previous with { Text = previous.Text + text };
        }
        else
        {
            output.Add(new TextRun(text, style));
        }
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var whitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = true;
                continue;
            }

            if (whitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            whitespace = false;
            builder.Append(character);
        }

        if (whitespace)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static void TrimInlines(List<InlineContent> inlines)
    {
        while (inlines.Count > 0 &&
               inlines[0] is TextRun first &&
               string.IsNullOrWhiteSpace(first.Text))
        {
            inlines.RemoveAt(0);
        }

        while (inlines.Count > 0 &&
               inlines[^1] is TextRun last &&
               string.IsNullOrWhiteSpace(last.Text))
        {
            inlines.RemoveAt(inlines.Count - 1);
        }

        if (inlines.Count > 0 && inlines[0] is TextRun firstRun)
        {
            inlines[0] = firstRun with { Text = firstRun.Text.TrimStart() };
        }

        if (inlines.Count > 0 && inlines[^1] is TextRun lastRun)
        {
            inlines[^1] = lastRun with { Text = lastRun.Text.TrimEnd() };
        }
    }

    private static bool HasReadableContent(IEnumerable<InlineContent> inlines) =>
        inlines.Any(inline => inline switch
        {
            TextRun run => !string.IsNullOrWhiteSpace(run.Text),
            InlineImage => true,
            _ => false
        });

    private static bool ShouldSkip(IElement element) =>
        element.TagName is "SCRIPT" or "STYLE" or "NOSCRIPT" or "IFRAME" or
            "OBJECT" or "EMBED" or "CANVAS" or "AUDIO" or "VIDEO" or "SOURCE" ||
        element.HasAttribute("hidden") ||
        element.GetAttribute("aria-hidden") == "true";

    private static TextStyle ApplyStyle(IElement element, TextStyle style)
    {
        style |= element.TagName switch
        {
            "B" or "STRONG" => TextStyle.Bold,
            "I" or "EM" or "CITE" => TextStyle.Italic,
            "U" or "INS" => TextStyle.Underline,
            "S" or "STRIKE" or "DEL" => TextStyle.Strikethrough,
            "SUP" => TextStyle.Superscript,
            "SUB" => TextStyle.Subscript,
            "CODE" or "KBD" or "SAMP" or "TT" => TextStyle.Code,
            _ => TextStyle.None
        };

        var inlineStyle = element.GetAttribute("style") ?? string.Empty;
        if (inlineStyle.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase) ||
            inlineStyle.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase))
        {
            style |= TextStyle.Bold;
        }

        if (inlineStyle.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase) ||
            inlineStyle.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase))
        {
            style |= TextStyle.Italic;
        }

        if (inlineStyle.Contains("underline", StringComparison.OrdinalIgnoreCase))
        {
            style |= TextStyle.Underline;
        }

        if (inlineStyle.Contains("line-through", StringComparison.OrdinalIgnoreCase))
        {
            style |= TextStyle.Strikethrough;
        }

        return style;
    }

    private static TextAlignmentKind ParseAlignment(IElement element)
    {
        var value = element.GetAttribute("align") ?? element.GetAttribute("style") ?? string.Empty;
        if (value.Contains("center", StringComparison.OrdinalIgnoreCase))
        {
            return TextAlignmentKind.Center;
        }

        if (value.Contains("justify", StringComparison.OrdinalIgnoreCase))
        {
            return TextAlignmentKind.Justify;
        }

        if (value.Contains("right", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("end", StringComparison.OrdinalIgnoreCase))
        {
            return TextAlignmentKind.End;
        }

        return TextAlignmentKind.Start;
    }

    private static int? ParsePositiveInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        number > 0
            ? number
            : null;

    private static double? ParseDimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var number = new string(value
            .TakeWhile(character => char.IsDigit(character) || character is '.' or '-')
            .ToArray());
        return double.TryParse(
            number,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0
                ? parsed
                : null;
    }
}

internal static class InlineCssImageParser
{
    public static bool TryExtractBackgroundImage(
        string? inlineStyle,
        out string reference)
    {
        reference = string.Empty;
        if (string.IsNullOrWhiteSpace(inlineStyle))
        {
            return false;
        }

        string? candidate = null;
        var declarationStart = 0;
        while (declarationStart < inlineStyle.Length)
        {
            var declarationEnd = FindDeclarationEnd(inlineStyle, declarationStart);
            var declaration = inlineStyle
                .AsSpan(declarationStart, declarationEnd - declarationStart)
                .Trim();
            var colon = declaration.IndexOf(':');
            if (colon >= 0)
            {
                var property = declaration[..colon].Trim();
                if (property.Equals(
                        "background-image",
                        StringComparison.OrdinalIgnoreCase) ||
                    property.Equals(
                        "background",
                        StringComparison.OrdinalIgnoreCase))
                {
                    candidate = TryExtractUrl(
                            declaration[(colon + 1)..],
                            out var parsed)
                        ? parsed
                        : null;
                }
            }

            declarationStart = declarationEnd + 1;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        reference = candidate;
        return true;
    }

    private static int FindDeclarationEnd(string style, int start)
    {
        char quote = '\0';
        var parentheses = 0;
        for (var index = start; index < style.Length; index++)
        {
            var character = style[index];
            if (quote != '\0')
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character == '/' &&
                index + 1 < style.Length &&
                style[index + 1] == '*')
            {
                var commentEnd = style.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return style.Length;
                }

                index = commentEnd + 1;
                continue;
            }

            if (character == '(')
            {
                parentheses++;
            }
            else if (character == ')' && parentheses > 0)
            {
                parentheses--;
            }
            else if (character == ';' && parentheses == 0)
            {
                return index;
            }
        }

        return style.Length;
    }

    private static bool TryExtractUrl(
        ReadOnlySpan<char> value,
        out string reference)
    {
        reference = string.Empty;
        for (var index = 0; index + 3 <= value.Length; index++)
        {
            if ((index > 0 && IsCssNameCharacter(value[index - 1])) ||
                !value.Slice(index, 3).Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cursor = index + 3;
            if (cursor < value.Length && IsCssNameCharacter(value[cursor]))
            {
                continue;
            }

            SkipWhitespaceAndComments(value, ref cursor);
            if (cursor >= value.Length || value[cursor] != '(')
            {
                continue;
            }

            if (TryReadUrlFunction(value, cursor, out reference))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadUrlFunction(
        ReadOnlySpan<char> value,
        int openingParenthesis,
        out string reference)
    {
        reference = string.Empty;
        var cursor = openingParenthesis + 1;
        SkipWhitespaceAndComments(value, ref cursor);
        if (cursor >= value.Length)
        {
            return false;
        }

        ReadOnlySpan<char> rawReference;
        var quote = value[cursor];
        if (quote is '\'' or '"')
        {
            var contentStart = ++cursor;
            while (cursor < value.Length)
            {
                if (value[cursor] == '\\')
                {
                    cursor += 2;
                    continue;
                }

                if (value[cursor] == quote)
                {
                    break;
                }

                cursor++;
            }

            if (cursor >= value.Length)
            {
                return false;
            }

            rawReference = value[contentStart..cursor];
            cursor++;
            SkipWhitespaceAndComments(value, ref cursor);
            if (cursor >= value.Length || value[cursor] != ')')
            {
                return false;
            }
        }
        else
        {
            var contentStart = cursor;
            while (cursor < value.Length && value[cursor] != ')')
            {
                if (value[cursor] == '\\' && cursor + 1 < value.Length)
                {
                    cursor += 2;
                }
                else
                {
                    cursor++;
                }
            }

            if (cursor >= value.Length)
            {
                return false;
            }

            rawReference = value[contentStart..cursor].Trim();
        }

        reference = UnescapeCss(rawReference).Trim();
        return reference.Length > 0;
    }

    private static void SkipWhitespaceAndComments(
        ReadOnlySpan<char> value,
        ref int cursor)
    {
        while (cursor < value.Length)
        {
            if (char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < value.Length &&
                value[cursor] == '/' &&
                value[cursor + 1] == '*')
            {
                var remainder = value[(cursor + 2)..];
                var commentEnd = remainder.IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                cursor = commentEnd < 0
                    ? value.Length
                    : cursor + 2 + commentEnd + 2;
                continue;
            }

            break;
        }
    }

    private static string UnescapeCss(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value.ToString();
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                builder.Append(character);
                continue;
            }

            index++;
            if (value[index] == '\r')
            {
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (value[index] == '\n' || value[index] == '\f')
            {
                continue;
            }

            if (!Uri.IsHexDigit(value[index]))
            {
                builder.Append(value[index]);
                continue;
            }

            var codePoint = 0;
            var digits = 0;
            while (index < value.Length &&
                   digits < 6 &&
                   Uri.IsHexDigit(value[index]))
            {
                codePoint = (codePoint * 16) + HexValue(value[index]);
                digits++;
                index++;
            }

            if (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                if (value[index] == '\r' &&
                    index + 1 < value.Length &&
                    value[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                index--;
            }

            builder.Append(
                codePoint is <= 0 or > 0x10ffff or >= 0xd800 and <= 0xdfff
                    ? "\ufffd"
                    : char.ConvertFromUtf32(codePoint));
        }

        return builder.ToString();
    }

    private static int HexValue(char value) =>
        value is >= '0' and <= '9'
            ? value - '0'
            : char.ToUpperInvariant(value) - 'A' + 10;

    private static bool IsCssNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '-' or '_' || value >= '\u0080';
}
