using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using OpenShelf.Core;

namespace OpenShelf.Formats;

internal static class FormatUtilities
{
    private const int BufferSize = 81_920;

    public static FileInfo ValidateInput(BookImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new ArgumentException("An input path is required.", nameof(request));
        }

        var file = new FileInfo(request.FilePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected book no longer exists.", file.FullName);
        }

        if (file.Length > request.Limits.MaximumInputBytes)
        {
            throw FormatImportException.Unsupported(
                $"The book is {file.Length:N0} bytes; the configured limit is " +
                $"{request.Limits.MaximumInputBytes:N0} bytes.");
        }

        return file;
    }

    public static async Task<string> ComputeRevisionHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<byte[]> ReadAllBytesLimitedAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected book no longer exists.", path);
        }

        if (file.Length > maximumBytes || file.Length > int.MaxValue)
        {
            throw FormatImportException.Unsupported(
                $"The input exceeds the configured {maximumBytes:N0}-byte limit.");
        }

        var result = GC.AllocateUninitializedArray<byte>((int)file.Length);
        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static string StableId(string prefix, params string?[] values)
    {
        var canonical = string.Join('\u001f', values.Select(value => value ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}-{Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    public static string FallbackTitle(BookImportRequest request) =>
        string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileNameWithoutExtension(request.FilePath)
            : Path.GetFileNameWithoutExtension(request.DisplayName);

    public static string CleanText(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Replace('\u00a0', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static XmlReaderSettings SafeXmlSettings(long maximumCharacters)
    {
        return new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = Math.Max(1, maximumCharacters)
        };
    }

    public static void ValidateXmlDepth(
        ReadOnlyMemory<byte> data,
        ImportSecurityLimits limits)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = XmlReader.Create(
            stream,
            SafeXmlSettings(Math.Min(limits.MaximumExpandedBytes, data.Length + 1L)));
        while (reader.Read())
        {
            if (reader.Depth > limits.MaximumXmlDepth)
            {
                throw FormatImportException.Corrupt(
                    $"XML nesting exceeds the configured depth of {limits.MaximumXmlDepth}.");
            }
        }
    }

    public static ReflowableDocument CreateReflowableDocument(
        BookMetadata metadata,
        string revisionHash,
        IEnumerable<DocumentSection> sections,
        IEnumerable<TableOfContentsItem>? tableOfContents = null,
        IEnumerable<BookResource>? resources = null)
    {
        var normalizedStart = 0L;
        var finalizedSections = sections.Select(section =>
        {
            var length = MeasureBlocks(section.Blocks);
            var finalized = section with
            {
                NormalizedStart = normalizedStart,
                NormalizedLength = length
            };
            normalizedStart += length;
            return finalized;
        }).ToImmutableArray();

        var resourceMap = (resources ?? [])
            .GroupBy(resource => resource.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableDictionary(resource => resource.Id, StringComparer.Ordinal);

        return new ReflowableDocument(
            metadata,
            revisionHash,
            normalizedStart,
            finalizedSections,
            (tableOfContents ?? []).ToImmutableArray(),
            resourceMap);
    }

    public static long MeasureBlocks(IEnumerable<DocumentBlock> blocks)
    {
        long length = 0;
        foreach (var block in blocks)
        {
            length += block switch
            {
                HeadingBlock heading => DocumentText.Flatten(heading.Content).Length + 1L,
                ParagraphBlock paragraph => DocumentText.Flatten(paragraph.Content).Length + 1L,
                QuoteBlock quote => MeasureBlocks(quote.Blocks) + 1L,
                ListBlock list => list.Items.Sum(item => MeasureBlocks(item.Blocks) + 1L),
                TableBlock table => table.Rows.Sum(row =>
                    row.Cells.Sum(cell => MeasureBlocks(cell.Blocks) + 1L)),
                ImageBlock image => (image.AlternativeText?.Length ?? 0) + 1L,
                PageBreakBlock => 1L,
                HorizontalRuleBlock => 1L,
                _ => 0L
            };
        }

        return length;
    }

    public static string? DetectMediaType(ReadOnlySpan<byte> data, string? path = null)
    {
        if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }

        if (data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return "image/jpeg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        if (data.Length >= 12 &&
            data[..4].SequenceEqual("RIFF"u8) &&
            data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (LooksLikeSvg(data))
        {
            return "image/svg+xml";
        }

        return Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" or ".svgz" => "image/svg+xml",
            _ => null
        };
    }

    public static void ValidateImage(
        byte[] data,
        string mediaType,
        ImportSecurityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.LongLength > limits.MaximumResourceBytes)
        {
            throw FormatImportException.Unsupported(
                $"An embedded image exceeds the {limits.MaximumResourceBytes:N0}-byte limit.");
        }

        if (mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            var dimensions = SvgImageDimensions.Read(data, limits);
            ValidateDecodedPixelCount(dimensions.Width, dimensions.Height, limits);
            return;
        }

        var hasDimensions = ImageDimensions.TryRead(data, mediaType, out var width, out var height);
        if (mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) && !hasDimensions)
        {
            throw FormatImportException.Corrupt(
                "An embedded WebP image has an invalid or unsupported header.");
        }

        if (hasDimensions)
        {
            ValidateDecodedPixelCount(width, height, limits);
        }
    }

    private static void ValidateDecodedPixelCount(
        double width,
        double height,
        ImportSecurityLimits limits)
    {
        if (!double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            throw FormatImportException.Corrupt(
                "An embedded image declares invalid dimensions.");
        }

        var decodedWidth = Math.Ceiling(width);
        var decodedHeight = Math.Ceiling(height);
        if (decodedWidth * decodedHeight > limits.MaximumDecodedPixels)
        {
            throw FormatImportException.Unsupported(
                $"An embedded image expands to more than " +
                $"{limits.MaximumDecodedPixels:N0} pixels.");
        }
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var prefix = data[..Math.Min(data.Length, 512)];
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var text = encoding.GetString(prefix);
            if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class ArchivePath
{
    public static string NormalizeEntry(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            normalized.Contains(':'))
        {
            throw FormatImportException.Corrupt($"Unsafe archive entry path: {path}");
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw FormatImportException.Corrupt($"Unsafe archive entry path: {path}");
        }

        return string.Join('/', parts);
    }

    public static bool TryResolve(string baseEntryPath, string reference, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var withoutFragment = reference.Split('#', 2)[0].Split('?', 2)[0];
        try
        {
            withoutFragment = Uri.UnescapeDataString(withoutFragment);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (Uri.TryCreate(withoutFragment, UriKind.Absolute, out _) ||
            withoutFragment.StartsWith('/') ||
            withoutFragment.Contains('\\') ||
            withoutFragment.Contains(':'))
        {
            return false;
        }

        var baseParts = baseEntryPath.Replace('\\', '/').Split('/').ToList();
        if (baseParts.Count > 0)
        {
            baseParts.RemoveAt(baseParts.Count - 1);
        }

        foreach (var part in withoutFragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (baseParts.Count == 0)
                {
                    return false;
                }

                baseParts.RemoveAt(baseParts.Count - 1);
                continue;
            }

            baseParts.Add(part);
        }

        result = string.Join('/', baseParts);
        return result.Length > 0;
    }
}

internal sealed class SafeZipPackage : IDisposable
{
    private readonly FileStream stream;
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;
    private readonly ImportSecurityLimits limits;

    public SafeZipPackage(string path, ImportSecurityLimits limits)
    {
        this.limits = limits;
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected book no longer exists.", path);
        }

        if (file.Length > limits.MaximumInputBytes)
        {
            throw FormatImportException.Unsupported("The archive exceeds the input-size limit.");
        }

        try
        {
            stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.SequentialScan);
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > limits.MaximumArchiveEntries)
            {
                throw FormatImportException.Unsupported(
                    $"The archive contains more than {limits.MaximumArchiveEntries:N0} entries.");
            }

            long expanded = 0;
            entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                if (unixType == 0xa000)
                {
                    throw FormatImportException.Corrupt(
                        $"Symbolic links are not permitted in book archives: {entry.FullName}");
                }

                expanded = checked(expanded + entry.Length);
                if (expanded > limits.MaximumExpandedBytes)
                {
                    throw FormatImportException.Unsupported(
                        "The expanded archive exceeds the configured safety limit.");
                }

                var normalized = ArchivePath.NormalizeEntry(entry.FullName);
                if (!entries.TryAdd(normalized, entry))
                {
                    throw FormatImportException.Corrupt(
                        $"The archive contains duplicate entry path '{normalized}'.");
                }
            }
        }
        catch
        {
            archive?.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<string> Paths => entries.Keys;

    public bool Contains(string path) => entries.ContainsKey(path);

    public long GetLength(string path) =>
        entries.TryGetValue(path, out var entry) ? entry.Length : -1;

    public async Task<byte[]> ReadAsync(
        string path,
        CancellationToken cancellationToken,
        long? maximumBytes = null)
    {
        if (!entries.TryGetValue(path, out var entry))
        {
            throw FormatImportException.Corrupt(
                $"Required archive entry '{path}' is missing.");
        }

        var maximum = maximumBytes ?? limits.MaximumResourceBytes;
        if (entry.Length > maximum || entry.Length > int.MaxValue)
        {
            throw FormatImportException.Unsupported(
                $"Archive entry '{path}' exceeds the {maximum:N0}-byte limit.");
        }

        var result = GC.AllocateUninitializedArray<byte>((int)entry.Length);
        await using var entryStream = entry.Open();
        await entryStream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void Dispose()
    {
        archive.Dispose();
        stream.Dispose();
    }
}

internal static class ImageDimensions
{
    public static bool TryRead(
        ReadOnlySpan<byte> data,
        string mediaType,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (mediaType == "image/png" && data.Length >= 24)
        {
            width = ReadBigEndianInt32(data.Slice(16, 4));
            height = ReadBigEndianInt32(data.Slice(20, 4));
            return width > 0 && height > 0;
        }

        if (mediaType == "image/gif" && data.Length >= 10)
        {
            width = data[6] | (data[7] << 8);
            height = data[8] | (data[9] << 8);
            return width > 0 && height > 0;
        }

        if (mediaType == "image/bmp" && data.Length >= 26)
        {
            width = Math.Abs(BitConverter.ToInt32(data.Slice(18, 4)));
            height = Math.Abs(BitConverter.ToInt32(data.Slice(22, 4)));
            return width > 0 && height > 0;
        }

        if (mediaType == "image/jpeg")
        {
            return TryReadJpeg(data, out width, out height);
        }

        if (mediaType == "image/webp")
        {
            return TryReadWebP(data, out width, out height);
        }

        return false;
    }

    private static bool TryReadWebP(
        ReadOnlySpan<byte> data,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 12 ||
            !data[..4].SequenceEqual("RIFF"u8) ||
            !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var declaredEnd = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) + 8L;
        if (declaredEnd < 12 || declaredEnd > data.Length)
        {
            return false;
        }

        var end = (int)declaredEnd;
        var offset = 12;
        while (offset + 8 <= end)
        {
            var chunkName = data.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payloadOffset = offset + 8;
            var payloadEnd = payloadOffset + (long)chunkLength;
            if (payloadEnd > end)
            {
                return false;
            }

            var payload = data.Slice(payloadOffset, (int)chunkLength);
            if (chunkName.SequenceEqual("VP8X"u8) && payload.Length >= 10)
            {
                width = 1 + ReadLittleEndianUInt24(payload.Slice(4, 3));
                height = 1 + ReadLittleEndianUInt24(payload.Slice(7, 3));
                return true;
            }

            if (chunkName.SequenceEqual("VP8 "u8) &&
                payload.Length >= 10 &&
                payload[3] == 0x9d &&
                payload[4] == 0x01 &&
                payload[5] == 0x2a)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff;
                return width > 0 && height > 0;
            }

            if (chunkName.SequenceEqual("VP8L"u8) &&
                payload.Length >= 5 &&
                payload[0] == 0x2f)
            {
                var sizeBits = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                width = 1 + (int)(sizeBits & 0x3fff);
                height = 1 + (int)((sizeBits >> 14) & 0x3fff);
                return true;
            }

            var paddedEnd = payloadEnd + (chunkLength & 1);
            if (paddedEnd > end)
            {
                return false;
            }

            offset = (int)paddedEnd;
        }

        return false;
    }

    private static bool TryReadJpeg(
        ReadOnlySpan<byte> data,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        var offset = 2;
        while (offset + 9 < data.Length)
        {
            if (data[offset++] != 0xff)
            {
                continue;
            }

            while (offset < data.Length && data[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                return false;
            }

            var marker = data[offset++];
            if (marker is 0xd8 or 0xd9)
            {
                continue;
            }

            if (offset + 2 > data.Length)
            {
                return false;
            }

            var segmentLength = (data[offset] << 8) | data[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > data.Length)
            {
                return false;
            }

            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7
                or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = (data[offset + 3] << 8) | data[offset + 4];
                width = (data[offset + 5] << 8) | data[offset + 6];
                return width > 0 && height > 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static int ReadLittleEndianUInt24(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}

internal static class SvgImageDimensions
{
    public static (double Width, double Height) Read(
        byte[] data,
        ImportSecurityLimits limits)
    {
        double? width = null;
        double? height = null;
        double? viewBoxWidth = null;
        double? viewBoxHeight = null;
        var foundRoot = false;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(
                stream,
                FormatUtilities.SafeXmlSettings(
                    Math.Min(limits.MaximumResourceBytes, data.LongLength + 1)));
            while (reader.Read())
            {
                if (reader.Depth > limits.MaximumXmlDepth)
                {
                    throw FormatImportException.Corrupt(
                        $"SVG nesting exceeds the configured depth of {limits.MaximumXmlDepth}.");
                }

                if (!foundRoot && reader.NodeType == XmlNodeType.Element)
                {
                    if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                    {
                        throw FormatImportException.Corrupt(
                            "The embedded SVG does not have an SVG root element.");
                    }

                    foundRoot = true;
                    if (!TryParseLength(reader.GetAttribute("width"), out width) ||
                        !TryParseLength(reader.GetAttribute("height"), out height) ||
                        !TryParseViewBox(
                            reader.GetAttribute("viewBox"),
                            out viewBoxWidth,
                            out viewBoxHeight))
                    {
                        throw FormatImportException.Corrupt(
                            "The embedded SVG declares invalid dimensions.");
                    }
                }
            }
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw FormatImportException.Corrupt(
                "The embedded SVG is malformed.",
                exception);
        }

        if (!foundRoot)
        {
            throw FormatImportException.Corrupt(
                "The embedded SVG does not have an SVG root element.");
        }

        return (
            width ?? viewBoxWidth ?? 300,
            height ?? viewBoxHeight ?? 150);
    }

    private static bool TryParseLength(string? value, out double? length)
    {
        length = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        if (text.EndsWith('%'))
        {
            return true;
        }

        var scale = 1d;
        var number = text;
        foreach (var unit in Units)
        {
            if (!text.EndsWith(unit.Suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            number = text[..^unit.Suffix.Length].TrimEnd();
            scale = unit.Scale;
            break;
        }

        if (!double.TryParse(
                number,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        length = parsed * scale;
        return double.IsFinite(length.Value);
    }

    private static bool TryParseViewBox(
        string? value,
        out double? width,
        out double? height)
    {
        width = null;
        height = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var values = value.Split(
            [',', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 4 ||
            !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minimumX) ||
            !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minimumY) ||
            !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidth) ||
            !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeight) ||
            !double.IsFinite(minimumX) ||
            !double.IsFinite(minimumY) ||
            !double.IsFinite(parsedWidth) ||
            !double.IsFinite(parsedHeight) ||
            parsedWidth <= 0 ||
            parsedHeight <= 0)
        {
            return false;
        }

        width = parsedWidth;
        height = parsedHeight;
        return true;
    }

    private static readonly (string Suffix, double Scale)[] Units =
    [
        ("rem", 16),
        ("px", 1),
        ("in", 96),
        ("cm", 96 / 2.54),
        ("mm", 96 / 25.4),
        ("pt", 96 / 72),
        ("pc", 16),
        ("em", 16),
        ("ex", 8)
    ];
}
