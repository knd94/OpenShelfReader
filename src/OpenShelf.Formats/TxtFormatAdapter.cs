using System.Collections.Immutable;
using System.Text;
using OpenShelf.Core;

namespace OpenShelf.Formats;

public sealed class TxtFormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" };

    public BookFormat Format => BookFormat.Txt;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty ||
            header.StartsWith("%PDF-"u8) ||
            header.StartsWith("PK\u0003\u0004"u8) ||
            header.StartsWith("{\\rtf"u8))
        {
            return false;
        }

        var nulls = 0;
        foreach (var value in header)
        {
            if (value == 0)
            {
                nulls++;
            }
        }

        // UTF-16 text has regular nulls; arbitrary binary data generally has many.
        return nulls == 0 || nulls * 3 < header.Length;
    }

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Reading text", request.DisplayName, 0, null, 0));

        var bytes = await FormatUtilities.ReadAllBytesLimitedAsync(
            request.FilePath,
            request.Limits.MaximumInputBytes,
            cancellationToken).ConfigureAwait(false);
        var (text, encodingWarning) = DecodeText(bytes);
        var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
            request.FilePath,
            cancellationToken).ConfigureAwait(false);

        var blocks = ParseBlocks(text, request.FilePath);
        if (blocks.Length == 0)
        {
            blocks = [new ParagraphBlock(
                FormatUtilities.StableId("paragraph", request.FilePath, "empty"),
                [new TextRun(string.Empty)])];
        }

        var sectionId = FormatUtilities.StableId("section", revisionHash, "text");
        var section = new DocumentSection(sectionId, null, blocks, 0, 0);
        var metadata = new BookMetadata(
            FormatUtilities.FallbackTitle(request),
            "Unknown author");
        var document = FormatUtilities.CreateReflowableDocument(
            metadata,
            revisionHash,
            [section]);

        progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));
        return new ImportedBook(
            Format,
            metadata,
            document,
            null,
            null,
            encodingWarning is null ? [] : [encodingWarning]);
    }

    private static (string Text, string? Warning) DecodeText(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), null);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            return (Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4), null);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true)
                .GetString(bytes, 4, bytes.Length - 4), null);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), null);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), null);
        }

        if (LooksLikeUtf16(bytes, evenBytesAreNull: false))
        {
            return (Encoding.Unicode.GetString(bytes), "Text encoding inferred as UTF-16 LE.");
        }

        if (LooksLikeUtf16(bytes, evenBytesAreNull: true))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes), "Text encoding inferred as UTF-16 BE.");
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            return (strictUtf8.GetString(bytes), null);
        }
        catch (DecoderFallbackException)
        {
            return (
                Encoding.GetEncoding(1252).GetString(bytes),
                "Text was not valid UTF-8 and was decoded as Windows-1252.");
        }
    }

    private static bool LooksLikeUtf16(byte[] bytes, bool evenBytesAreNull)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var sampled = Math.Min(bytes.Length, 4096);
        var expectedNulls = 0;
        var unexpectedNulls = 0;
        for (var index = 0; index < sampled; index++)
        {
            var isExpectedPosition = (index % 2 == 0) == evenBytesAreNull;
            if (bytes[index] == 0)
            {
                if (isExpectedPosition)
                {
                    expectedNulls++;
                }
                else
                {
                    unexpectedNulls++;
                }
            }
        }

        return expectedNulls > sampled / 8 && unexpectedNulls < sampled / 32;
    }

    private static ImmutableArray<DocumentBlock> ParseBlocks(string text, string sourceKey)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var blocks = new List<DocumentBlock>();
        var paragraphLines = new List<string>();
        var index = 0;

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            var content = new List<InlineContent>();
            for (var lineIndex = 0; lineIndex < paragraphLines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    content.Add(new LineBreak());
                }

                content.Add(new TextRun(paragraphLines[lineIndex]));
            }

            blocks.Add(new ParagraphBlock(
                FormatUtilities.StableId(
                    "paragraph",
                    sourceKey,
                    (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                content.ToImmutableArray()));
            paragraphLines.Clear();
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var nextIsUnderline = lineIndex + 1 < lines.Length &&
                IsHeadingUnderline(lines[lineIndex + 1]);
            if ((nextIsUnderline || IsAllCapsHeading(line)) && paragraphLines.Count == 0)
            {
                var headingText = line.Trim();
                blocks.Add(new HeadingBlock(
                    FormatUtilities.StableId(
                        "heading",
                        sourceKey,
                        (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    nextIsUnderline && lines[lineIndex + 1].TrimStart().StartsWith('=')
                        ? 1
                        : 2,
                    [new TextRun(headingText)]));
                if (nextIsUnderline)
                {
                    lineIndex++;
                }

                continue;
            }

            paragraphLines.Add(line);
        }

        FlushParagraph();
        return blocks.ToImmutableArray();
    }

    private static bool IsHeadingUnderline(string line)
    {
        var value = line.Trim();
        return value.Length >= 3 &&
            value.All(character => character == value[0]) &&
            value[0] is '=' or '-';
    }

    private static bool IsAllCapsHeading(string line)
    {
        var value = line.Trim();
        var letters = value.Where(char.IsLetter).ToArray();
        return value.Length is > 0 and <= 100 &&
            letters.Length >= 2 &&
            letters.All(char.IsUpper);
    }
}
