using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenShelf.Core;

namespace OpenShelf.Formats;

public sealed class Fb2FormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".fb2" };

    public BookFormat Format => BookFormat.Fb2;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(header[..Math.Min(header.Length, 4096)]);
        return text.Contains("<FictionBook", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Reading FictionBook", request.DisplayName, 0, null, 0));

        var bytes = await FormatUtilities.ReadAllBytesLimitedAsync(
            request.FilePath,
            request.Limits.MaximumInputBytes,
            cancellationToken).ConfigureAwait(false);
        FormatUtilities.ValidateXmlDepth(bytes, request.Limits);

        XDocument xml;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(
                stream,
                FormatUtilities.SafeXmlSettings(
                    Math.Min(request.Limits.MaximumExpandedBytes, bytes.LongLength + 1)));
            xml = XDocument.Load(reader, LoadOptions.None);
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is XmlException or InvalidOperationException)
        {
            throw FormatImportException.Corrupt(
                "The FictionBook XML is malformed.",
                exception);
        }

        var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
            request.FilePath,
            cancellationToken).ConfigureAwait(false);
        var titleInfo = xml.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "title-info");
        var title = ChildValue(titleInfo, "book-title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = FormatUtilities.FallbackTitle(request);
        }

        var author = FormatAuthor(titleInfo);
        var metadata = new BookMetadata(
            FormatUtilities.CleanText(title),
            string.IsNullOrWhiteSpace(author) ? "Unknown author" : author,
            ChildValue(titleInfo, "annotation"),
            ChildValue(titleInfo, "lang"),
            xml.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "publish-info") is { } publishInfo
                    ? ChildValue(publishInfo, "publisher")
                    : null,
            xml.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "publish-info") is { } identifierInfo
                    ? ChildValue(identifierInfo, "isbn")
                    : null);

        var resources = ReadResources(xml, request.Limits);
        var resourceBySourceId = resources.ToDictionary(
            item => item.SourceId,
            item => item.Resource,
            StringComparer.Ordinal);
        var coverReference = titleInfo?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "coverpage")?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "image");
        var coverSourceId = GetHref(coverReference)?.TrimStart('#');
        var cover = coverSourceId is not null &&
            resourceBySourceId.TryGetValue(coverSourceId, out var coverResource)
                ? coverResource
                : null;

        var sectionBuilder = new Fb2SectionBuilder(revisionHash, resourceBySourceId);
        var toc = new List<TableOfContentsItem>();
        var bodies = xml.Root?
            .Elements()
            .Where(element => element.Name.LocalName == "body")
            .ToArray() ?? [];
        for (var bodyIndex = 0; bodyIndex < bodies.Length; bodyIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = bodies[bodyIndex];
            var bodyName = FormatUtilities.CleanText(body.Attribute("name")?.Value);
            var bodySections = body.Elements()
                .Where(element => element.Name.LocalName == "section")
                .ToArray();
            if (bodySections.Length == 0)
            {
                sectionBuilder.AddBody(body, bodyName, bodyIndex);
                continue;
            }

            for (var sectionIndex = 0; sectionIndex < bodySections.Length; sectionIndex++)
            {
                toc.Add(sectionBuilder.AddSection(
                    bodySections[sectionIndex],
                    $"{bodyIndex}/{sectionIndex}",
                    level: 1));
            }
        }

        if (sectionBuilder.Sections.Count == 0)
        {
            sectionBuilder.AddEmpty(title);
        }

        var document = FormatUtilities.CreateReflowableDocument(
            metadata,
            revisionHash,
            sectionBuilder.Sections,
            toc,
            resources.Select(item => item.Resource));
        progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));

        return new ImportedBook(
            Format,
            metadata,
            document,
            cover?.Data,
            cover?.MediaType,
            []);
    }

    private static List<Fb2Resource> ReadResources(
        XDocument document,
        ImportSecurityLimits limits)
    {
        var resources = new List<Fb2Resource>();
        foreach (var binary in document.Descendants()
                     .Where(element => element.Name.LocalName == "binary"))
        {
            var sourceId = binary.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                continue;
            }

            var declaredType = binary.Attribute("content-type")?.Value;
            byte[] data;
            try
            {
                var compact = string.Concat(binary.Value.Where(
                    character => !char.IsWhiteSpace(character)));
                if (compact.Length / 4L * 3L > limits.MaximumResourceBytes)
                {
                    throw FormatImportException.Unsupported(
                        "An FB2 binary resource exceeds the configured size limit.");
                }

                data = Convert.FromBase64String(compact);
            }
            catch (FormatException exception)
            {
                throw FormatImportException.Corrupt(
                    $"FB2 binary resource '{sourceId}' is not valid base64.",
                    exception);
            }

            var mediaType = FormatUtilities.DetectMediaType(data, sourceId) ??
                declaredType;
            if (string.IsNullOrWhiteSpace(mediaType) ||
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FormatUtilities.ValidateImage(data, mediaType, limits);
            resources.Add(new Fb2Resource(
                sourceId,
                new BookResource(
                    FormatUtilities.StableId("resource", sourceId),
                    mediaType,
                    data,
                    sourceId)));
        }

        return resources;
    }

    private static string FormatAuthor(XElement? titleInfo)
    {
        var authors = titleInfo?
            .Elements()
            .Where(element => element.Name.LocalName == "author")
            .Select(author =>
            {
                var name = new[]
                {
                    ChildValue(author, "first-name"),
                    ChildValue(author, "middle-name"),
                    ChildValue(author, "last-name")
                };
                var formatted = string.Join(
                    " ",
                    name.Where(part => !string.IsNullOrWhiteSpace(part)));
                return formatted.Length > 0
                    ? formatted
                    : ChildValue(author, "nickname");
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray() ?? [];
        return string.Join("; ", authors!);
    }

    private static string? ChildValue(XElement? parent, string localName)
    {
        var value = parent?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value;
        var cleaned = FormatUtilities.CleanText(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? GetHref(XElement? element) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == "href")?.Value;

    private sealed record Fb2Resource(string SourceId, BookResource Resource);

    private sealed class Fb2SectionBuilder
    {
        private readonly string revisionHash;
        private readonly IReadOnlyDictionary<string, BookResource> resources;
        private int blockIndex;

        public Fb2SectionBuilder(
            string revisionHash,
            IReadOnlyDictionary<string, BookResource> resources)
        {
            this.revisionHash = revisionHash;
            this.resources = resources;
        }

        public List<DocumentSection> Sections { get; } = [];

        public TableOfContentsItem AddSection(
            XElement section,
            string path,
            int level)
        {
            var sourceId = section.Attribute("id")?.Value;
            var sectionId = FormatUtilities.StableId(
                "section",
                revisionHash,
                sourceId ?? path);
            var titleElement = section.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "title");
            var title = FormatUtilities.CleanText(titleElement?.Value);
            if (title.Length == 0)
            {
                title = $"Section {Sections.Count + 1}";
            }

            var blocks = new List<DocumentBlock>();
            if (titleElement is not null)
            {
                var titleInlines = ParseInlines(titleElement);
                if (titleInlines.Length > 0)
                {
                    blocks.Add(new HeadingBlock(
                        NextBlockId("heading", sourceId ?? path),
                        Math.Clamp(level, 1, 6),
                        titleInlines));
                }
            }

            foreach (var child in section.Elements())
            {
                if (child == titleElement || child.Name.LocalName == "section")
                {
                    continue;
                }

                AppendElement(child, blocks, path);
            }

            Sections.Add(new DocumentSection(sectionId, title, blocks.ToImmutableArray(), 0, 0));

            var childToc = new List<TableOfContentsItem>();
            var children = section.Elements()
                .Where(element => element.Name.LocalName == "section")
                .ToArray();
            for (var index = 0; index < children.Length; index++)
            {
                childToc.Add(AddSection(children[index], $"{path}/{index}", level + 1));
            }

            return new TableOfContentsItem(
                FormatUtilities.StableId("toc", revisionHash, sourceId ?? path),
                title,
                sectionId,
                blocks.FirstOrDefault()?.Id,
                childToc.ToImmutableArray());
        }

        public void AddBody(XElement body, string? title, int bodyIndex)
        {
            var sectionId = FormatUtilities.StableId(
                "section",
                revisionHash,
                $"body/{bodyIndex}");
            var blocks = new List<DocumentBlock>();
            foreach (var child in body.Elements())
            {
                AppendElement(child, blocks, $"body/{bodyIndex}");
            }

            Sections.Add(new DocumentSection(
                sectionId,
                string.IsNullOrWhiteSpace(title) ? null : title,
                blocks.ToImmutableArray(),
                0,
                0));
        }

        public void AddEmpty(string title)
        {
            var sectionId = FormatUtilities.StableId("section", revisionHash, "empty");
            Sections.Add(new DocumentSection(
                sectionId,
                title,
                [new HeadingBlock(
                    NextBlockId("heading", "empty"),
                    1,
                    [new TextRun(title)])],
                0,
                0));
        }

        private void AppendElement(
            XElement element,
            List<DocumentBlock> blocks,
            string path)
        {
            switch (element.Name.LocalName)
            {
                case "p":
                {
                    var content = ParseInlines(element);
                    if (content.Length > 0)
                    {
                        blocks.Add(new ParagraphBlock(
                            NextBlockId("paragraph", path),
                            content,
                            ParseAlignment(element)));
                    }

                    break;
                }
                case "subtitle":
                {
                    var content = ParseInlines(element);
                    if (content.Length > 0)
                    {
                        blocks.Add(new HeadingBlock(
                            NextBlockId("heading", path),
                            3,
                            content));
                    }

                    break;
                }
                case "image":
                    if (CreateImage(element, null) is { } image)
                    {
                        blocks.Add(image);
                    }

                    break;
                case "epigraph":
                case "cite":
                case "annotation":
                {
                    var quoteBlocks = new List<DocumentBlock>();
                    foreach (var child in element.Elements())
                    {
                        AppendElement(child, quoteBlocks, path);
                    }

                    if (quoteBlocks.Count > 0)
                    {
                        blocks.Add(new QuoteBlock(
                            NextBlockId("quote", path),
                            quoteBlocks.ToImmutableArray(),
                            ChildValue(element, "text-author")));
                    }

                    break;
                }
                case "poem":
                case "stanza":
                    foreach (var child in element.Elements())
                    {
                        AppendElement(child, blocks, path);
                    }

                    break;
                case "title":
                    blocks.Add(new HeadingBlock(
                        NextBlockId("heading", path),
                        2,
                        ParseInlines(element)));
                    break;
                case "v":
                case "text-author":
                case "date":
                {
                    var content = ParseInlines(element);
                    if (content.Length > 0)
                    {
                        blocks.Add(new ParagraphBlock(
                            NextBlockId("paragraph", path),
                            content));
                    }

                    break;
                }
                case "table":
                {
                    var rows = element.Elements()
                        .Where(row => row.Name.LocalName == "tr")
                        .Select(row => new TableRow(
                            row.Elements()
                                .Where(cell => cell.Name.LocalName is "td" or "th")
                                .Select(cell => new TableCell(
                                    [new ParagraphBlock(
                                        NextBlockId("cell", path),
                                        ParseInlines(cell))],
                                    ParseSpan(cell, "colspan"),
                                    ParseSpan(cell, "rowspan"),
                                    cell.Name.LocalName == "th"))
                                .ToImmutableArray()))
                        .Where(row => row.Cells.Length > 0)
                        .ToImmutableArray();
                    if (rows.Length > 0)
                    {
                        blocks.Add(new TableBlock(NextBlockId("table", path), rows));
                    }

                    break;
                }
                case "empty-line":
                    blocks.Add(new ParagraphBlock(
                        NextBlockId("spacing", path),
                        [new LineBreak()]));
                    break;
                default:
                    foreach (var child in element.Elements())
                    {
                        AppendElement(child, blocks, path);
                    }

                    break;
            }
        }

        private ImmutableArray<InlineContent> ParseInlines(XElement element)
        {
            var output = new List<InlineContent>();
            AppendInlineNodes(element.Nodes(), TextStyle.None, output);
            return output.ToImmutableArray();
        }

        private void AppendInlineNodes(
            IEnumerable<XNode> nodes,
            TextStyle style,
            List<InlineContent> output)
        {
            foreach (var node in nodes)
            {
                if (node is XText text)
                {
                    var value = text.Value.Replace('\u00a0', ' ');
                    if (value.Length > 0)
                    {
                        output.Add(new TextRun(value, style));
                    }

                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                var nextStyle = style | (element.Name.LocalName switch
                {
                    "strong" => TextStyle.Bold,
                    "emphasis" => TextStyle.Italic,
                    "strikethrough" => TextStyle.Strikethrough,
                    "sub" => TextStyle.Subscript,
                    "sup" => TextStyle.Superscript,
                    "code" => TextStyle.Code,
                    _ => TextStyle.None
                });

                if (element.Name.LocalName == "image")
                {
                    var href = GetHref(element)?.TrimStart('#');
                    if (href is not null && resources.TryGetValue(href, out var resource))
                    {
                        output.Add(new InlineImage(resource.Id, null, null, null));
                    }

                    continue;
                }

                var isLink = element.Name.LocalName == "a";
                if (isLink && GetHref(element) is { } target)
                {
                    output.Add(new HyperlinkStart(
                        target,
                        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https" or "mailto"));
                }

                AppendInlineNodes(element.Nodes(), nextStyle, output);
                if (isLink)
                {
                    output.Add(new HyperlinkEnd());
                }
            }
        }

        private ImageBlock? CreateImage(XElement element, string? caption)
        {
            var sourceId = GetHref(element)?.TrimStart('#');
            if (sourceId is null || !resources.TryGetValue(sourceId, out var resource))
            {
                return null;
            }

            return new ImageBlock(
                NextBlockId("image", sourceId),
                resource.Id,
                element.Attribute("alt")?.Value,
                null,
                null,
                caption);
        }

        private string NextBlockId(string type, string source) =>
            FormatUtilities.StableId(
                type,
                revisionHash,
                source,
                (blockIndex++).ToString(CultureInfo.InvariantCulture));

        private static int ParseSpan(XElement element, string name) =>
            int.TryParse(
                element.Attribute(name)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) && value > 0
                    ? value
                    : 1;

        private static TextAlignmentKind ParseAlignment(XElement element) =>
            element.Attribute("style")?.Value.Contains(
                "text-align:center",
                StringComparison.OrdinalIgnoreCase) == true
                ? TextAlignmentKind.Center
                : TextAlignmentKind.Start;
    }
}
