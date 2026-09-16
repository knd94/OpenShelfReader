using System.Collections.Immutable;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenShelf.Core;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace OpenShelf.Formats;

public sealed class DocxFormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx" };

    public BookFormat Format => BookFormat.Docx;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith("PK\u0003\u0004"u8);

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Opening DOCX", request.DisplayName, 0, null, 0));
        var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
            request.FilePath,
            cancellationToken).ConfigureAwait(false);

        try
        {
            // Open XML performs its own package parsing, but first apply the same
            // bounded ZIP validation used by the other archive-based adapters.
            using (var package = new SafeZipPackage(request.FilePath, request.Limits))
            {
            }

            using var document = WordprocessingDocument.Open(
                request.FilePath,
                isEditable: false,
                new OpenSettings
                {
                    AutoSave = false,
                    MarkupCompatibilityProcessSettings =
                        new MarkupCompatibilityProcessSettings(
                            MarkupCompatibilityProcessMode.ProcessAllParts,
                            FileFormatVersions.Office2013)
                });
            var mainPart = document.MainDocumentPart ??
                throw FormatImportException.Corrupt("The DOCX has no main document part.");
            var body = mainPart.Document?.Body ??
                throw FormatImportException.Corrupt("The DOCX has no document body.");

            var images = await ReadImagesAsync(
                mainPart,
                request.Limits,
                cancellationToken).ConfigureAwait(false);
            var state = new DocxNormalizationState(revisionHash, mainPart, images);
            var sections = new List<DocumentSection>();
            var toc = new List<TableOfContentsItem>();
            var currentBlocks = new List<DocumentBlock>();
            string? currentTitle = null;
            var sectionIndex = 0;

            void FlushSection()
            {
                if (currentBlocks.Count == 0)
                {
                    return;
                }

                var sectionId = FormatUtilities.StableId(
                    "section",
                    revisionHash,
                    sectionIndex.ToString(CultureInfo.InvariantCulture));
                sections.Add(new DocumentSection(
                    sectionId,
                    currentTitle,
                    currentBlocks.ToImmutableArray(),
                    0,
                    0));
                if (!string.IsNullOrWhiteSpace(currentTitle))
                {
                    toc.Add(new TableOfContentsItem(
                        FormatUtilities.StableId("toc", revisionHash, sectionIndex.ToString(CultureInfo.InvariantCulture)),
                        currentTitle,
                        sectionId,
                        currentBlocks.FirstOrDefault()?.Id,
                        []));
                }

                sectionIndex++;
                currentBlocks = [];
                currentTitle = null;
            }

            var elements = body.ChildElements.ToArray();
            for (var index = 0; index < elements.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new BookImportProgress(
                    "Parsing DOCX",
                    null,
                    index,
                    elements.Length,
                    elements.Length == 0 ? 1 : (double)index / elements.Length));
                switch (elements[index])
                {
                    case Paragraph paragraph:
                    {
                        var headingLevel = GetHeadingLevel(paragraph);
                        if (headingLevel == 1 && currentBlocks.Count > 0)
                        {
                            FlushSection();
                        }

                        var block = state.ConvertParagraph(paragraph, headingLevel);
                        if (block is not null)
                        {
                            currentBlocks.Add(block);
                            if (headingLevel == 1 && block is HeadingBlock heading)
                            {
                                currentTitle = FormatUtilities.CleanText(
                                    DocumentText.Flatten(heading.Content));
                            }
                        }

                        break;
                    }
                    case Table table:
                        if (state.ConvertTable(table) is { } tableBlock)
                        {
                            currentBlocks.Add(tableBlock);
                        }

                        break;
                }
            }

            FlushSection();
            if (sections.Count == 0)
            {
                sections.Add(new DocumentSection(
                    FormatUtilities.StableId("section", revisionHash, "empty"),
                    null,
                    [new ParagraphBlock(
                        FormatUtilities.StableId("paragraph", revisionHash, "empty"),
                        [new TextRun(string.Empty)])],
                    0,
                    0));
            }

            var properties = document.PackageProperties;
            var metadata = new BookMetadata(
                Clean(properties.Title) ?? FormatUtilities.FallbackTitle(request),
                Clean(properties.Creator) ?? "Unknown author",
                Clean(properties.Description),
                Clean(properties.Language),
                null,
                Clean(properties.Identifier));
            var normalized = FormatUtilities.CreateReflowableDocument(
                metadata,
                revisionHash,
                sections,
                toc,
                images.Values.Select(image => image.Resource));
            var cover = images.Values.FirstOrDefault()?.Resource;
            var warnings = state.UnsupportedDrawingCount > 0
                ? ImmutableArray.Create(
                    $"{state.UnsupportedDrawingCount} unsupported drawing object(s) were omitted.")
                : [];
            progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));
            return new ImportedBook(
                Format,
                metadata,
                normalized,
                cover?.Data,
                cover?.MediaType,
                warnings);
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (OpenXmlPackageException exception)
        {
            throw FormatImportException.Corrupt("The DOCX package is malformed.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw FormatImportException.Corrupt("The DOCX ZIP package is malformed.", exception);
        }
    }

    private static async Task<Dictionary<string, DocxImage>> ReadImagesAsync(
        MainDocumentPart mainPart,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DocxImage>(StringComparer.Ordinal);
        foreach (var imagePart in mainPart.ImageParts)
        {
            var relationshipId = mainPart.GetIdOfPart(imagePart);
            await using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            if (stream.Length > limits.MaximumResourceBytes || stream.Length > int.MaxValue)
            {
                continue;
            }

            var data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            var mediaType = FormatUtilities.DetectMediaType(data) ?? imagePart.ContentType;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FormatUtilities.ValidateImage(data, mediaType, limits);
            ImageDimensions.TryRead(data, mediaType, out var width, out var height);
            result[relationshipId] = new DocxImage(
                width > 0 ? width : null,
                height > 0 ? height : null,
                new BookResource(
                    FormatUtilities.StableId("resource", relationshipId),
                    mediaType,
                    data,
                    relationshipId));
        }

        return result;
    }

    private static int? GetHeadingLevel(Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(style) &&
            style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                style.AsSpan("Heading".Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var styleLevel))
        {
            return Math.Clamp(styleLevel, 1, 6);
        }

        if (paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value is { } outline)
        {
            return Math.Clamp(outline + 1, 1, 6);
        }

        return null;
    }

    private static string? Clean(string? value)
    {
        var cleaned = FormatUtilities.CleanText(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private sealed record DocxImage(
        double? Width,
        double? Height,
        BookResource Resource);

    private sealed class DocxNormalizationState
    {
        private readonly string revisionHash;
        private readonly MainDocumentPart mainPart;
        private readonly IReadOnlyDictionary<string, DocxImage> images;
        private int blockIndex;

        public DocxNormalizationState(
            string revisionHash,
            MainDocumentPart mainPart,
            IReadOnlyDictionary<string, DocxImage> images)
        {
            this.revisionHash = revisionHash;
            this.mainPart = mainPart;
            this.images = images;
        }

        public int UnsupportedDrawingCount { get; private set; }

        public DocumentBlock? ConvertParagraph(Paragraph paragraph, int? headingLevel)
        {
            var content = ParseParagraphContent(paragraph);
            if (content.Length == 0)
            {
                return null;
            }

            var id = NextId(headingLevel is null ? "paragraph" : "heading");
            if (headingLevel is not null)
            {
                return new HeadingBlock(id, headingLevel.Value, content);
            }

            var paragraphBlock = new ParagraphBlock(
                id,
                content,
                ParseAlignment(paragraph));
            if (paragraph.ParagraphProperties?.NumberingProperties is not null)
            {
                return new ListBlock(
                    NextId("list"),
                    IsOrderedList(paragraph),
                    1,
                    [new ListItemBlock([paragraphBlock])]);
            }

            return paragraphBlock;
        }

        public TableBlock? ConvertTable(Table table)
        {
            var rows = table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>()
                .Select(row => new OpenShelf.Core.TableRow(
                    row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                        .Select(cell =>
                        {
                            var blocks = cell.Elements<Paragraph>()
                                .Select(paragraph => ConvertParagraph(paragraph, GetHeadingLevel(paragraph)))
                                .Where(block => block is not null)
                                .Cast<DocumentBlock>()
                                .ToImmutableArray();
                            var span = cell.TableCellProperties?
                                .GridSpan?
                                .Val?
                                .Value ?? 1;
                            return new OpenShelf.Core.TableCell(
                                blocks,
                                Math.Max(1, span),
                                1,
                                false);
                        })
                        .ToImmutableArray()))
                .Where(row => row.Cells.Length > 0)
                .ToImmutableArray();
            return rows.Length == 0 ? null : new TableBlock(NextId("table"), rows);
        }

        private ImmutableArray<InlineContent> ParseParagraphContent(Paragraph paragraph)
        {
            var output = new List<InlineContent>();
            foreach (var child in paragraph.ChildElements)
            {
                switch (child)
                {
                    case Run run:
                        AppendRun(run, output);
                        break;
                    case Hyperlink hyperlink:
                    {
                        var relationship = hyperlink.Id?.Value is { } id
                            ? mainPart.HyperlinkRelationships.FirstOrDefault(item => item.Id == id)
                            : null;
                        var target = relationship?.Uri.ToString() ??
                            (hyperlink.Anchor?.Value is { } anchor ? $"#{anchor}" : null);
                        if (target is not null)
                        {
                            output.Add(new HyperlinkStart(
                                target,
                                relationship?.IsExternal == true));
                        }

                        foreach (var run in hyperlink.Elements<Run>())
                        {
                            AppendRun(run, output);
                        }

                        if (target is not null)
                        {
                            output.Add(new HyperlinkEnd());
                        }

                        break;
                    }
                    case SimpleField field:
                        foreach (var run in field.Elements<Run>())
                        {
                            AppendRun(run, output);
                        }

                        break;
                }
            }

            return output.ToImmutableArray();
        }

        private void AppendRun(Run run, List<InlineContent> output)
        {
            var style = ParseRunStyle(run.RunProperties);
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text text:
                        output.Add(new TextRun(text.Text, style));
                        break;
                    case TabChar:
                        output.Add(new TextRun("\t", style));
                        break;
                    case Break:
                    case CarriageReturn:
                        output.Add(new LineBreak());
                        break;
                    case Drawing drawing:
                    {
                        var relationshipId = drawing.Descendants<A.Blip>()
                            .FirstOrDefault()?
                            .Embed?
                            .Value;
                        if (relationshipId is not null &&
                            images.TryGetValue(relationshipId, out var image))
                        {
                            var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
                            output.Add(new InlineImage(
                                image.Resource.Id,
                                drawing.Descendants<DW.DocProperties>()
                                    .FirstOrDefault()?
                                    .Description?
                                    .Value,
                                extent?.Cx?.Value is { } width ? width / 9525d : image.Width,
                                extent?.Cy?.Value is { } height ? height / 9525d : image.Height));
                        }
                        else
                        {
                            UnsupportedDrawingCount++;
                        }

                        break;
                    }
                }
            }
        }

        private static TextStyle ParseRunStyle(RunProperties? properties)
        {
            if (properties is null)
            {
                return TextStyle.None;
            }

            var style = TextStyle.None;
            if (properties.Bold is not null)
            {
                style |= TextStyle.Bold;
            }

            if (properties.Italic is not null)
            {
                style |= TextStyle.Italic;
            }

            if (properties.Underline is not null)
            {
                style |= TextStyle.Underline;
            }

            if (properties.Strike is not null || properties.DoubleStrike is not null)
            {
                style |= TextStyle.Strikethrough;
            }

            var vertical = properties.VerticalTextAlignment?.Val?.Value.ToString();
            if (vertical?.Contains("Super", StringComparison.OrdinalIgnoreCase) == true)
            {
                style |= TextStyle.Superscript;
            }
            else if (vertical?.Contains("Sub", StringComparison.OrdinalIgnoreCase) == true)
            {
                style |= TextStyle.Subscript;
            }

            return style;
        }

        private static TextAlignmentKind ParseAlignment(Paragraph paragraph)
        {
            var alignment = paragraph.ParagraphProperties?
                .Justification?
                .Val?
                .Value
                .ToString();
            return alignment?.ToLowerInvariant() switch
            {
                "center" => TextAlignmentKind.Center,
                "right" or "end" => TextAlignmentKind.End,
                "both" or "distribute" => TextAlignmentKind.Justify,
                _ => TextAlignmentKind.Start
            };
        }

        private static bool IsOrderedList(Paragraph paragraph)
        {
            // Resolving numbering definitions exactly is intentionally left to the
            // layout layer; this identifies common decimal numbering styles.
            var numberId = paragraph.ParagraphProperties?
                .NumberingProperties?
                .NumberingId?
                .Val?
                .Value;
            return numberId is > 0;
        }

        private string NextId(string type) =>
            FormatUtilities.StableId(
                type,
                revisionHash,
                (blockIndex++).ToString(CultureInfo.InvariantCulture));
    }
}
