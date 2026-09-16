using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenShelf.Core;
using RtfPipe;

namespace OpenShelf.Formats;

public sealed partial class RtfFormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rtf" };

    public BookFormat Format => BookFormat.Rtf;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith("{\\rtf"u8);

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Reading RTF", request.DisplayName, 0, null, 0));
        var bytes = await FormatUtilities.ReadAllBytesLimitedAsync(
            request.FilePath,
            request.Limits.MaximumInputBytes,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesSignature(bytes.AsSpan(0, Math.Min(bytes.Length, 16))))
        {
            throw FormatImportException.Corrupt("The file does not have an RTF signature.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var rtf = DecodeRtfContainer(bytes);
        string generatedMarkup;
        try
        {
            generatedMarkup = Rtf.ToHtml(rtf);
        }
        catch (Exception exception)
        {
            throw FormatImportException.Corrupt("The RTF stream is malformed.", exception);
        }

        var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
            request.FilePath,
            cancellationToken).ConfigureAwait(false);
        var resources = new Dictionary<string, BookResource>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var normalized = MarkupNormalizer.Normalize(
            generatedMarkup,
            request.FilePath,
            (reference, alternativeText) =>
                ResolveDataImage(
                    reference,
                    alternativeText,
                    resources,
                    request.Limits,
                    warnings));
        var blocks = normalized.Blocks.ToList();

        // Some RTF producers emit picture groups that RtfPipe cannot translate.
        // Extract supported raster payloads as a deterministic fallback.
        foreach (var resource in ExtractPictureGroups(rtf, request.Limits, warnings))
        {
            if (resources.TryAdd(resource.Id, resource))
            {
                blocks.Add(new ImageBlock(
                    FormatUtilities.StableId("image", resource.Id),
                    resource.Id,
                    null,
                    null,
                    null));
            }
        }

        if (blocks.Count == 0)
        {
            throw FormatImportException.Corrupt("The RTF contains no readable content.");
        }

        var title = ExtractInfo(rtf, "title") ?? FormatUtilities.FallbackTitle(request);
        var author = ExtractInfo(rtf, "author") ?? "Unknown author";
        var metadata = new BookMetadata(title, author);
        var sectionId = FormatUtilities.StableId("section", revisionHash, "rtf");
        var document = FormatUtilities.CreateReflowableDocument(
            metadata,
            revisionHash,
            [new DocumentSection(
                sectionId,
                normalized.FirstHeading,
                blocks.ToImmutableArray(),
                0,
                0)],
            normalized.FirstHeading is { Length: > 0 } heading
                ? [new TableOfContentsItem(
                    FormatUtilities.StableId("toc", revisionHash, "rtf"),
                    heading,
                    sectionId,
                    blocks.FirstOrDefault()?.Id,
                    [])]
                : [],
            resources.Values);
        var cover = resources.Values.FirstOrDefault();
        progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));
        return new ImportedBook(
            Format,
            metadata,
            document,
            cover?.Data,
            cover?.MediaType,
            warnings.ToImmutableArray());
    }

    private static ResolvedImage? ResolveDataImage(
        string reference,
        string? alternativeText,
        IDictionary<string, BookResource> resources,
        ImportSecurityLimits limits,
        ICollection<string> warnings)
    {
        if (!reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = reference.IndexOf(',');
        if (comma <= 5)
        {
            return null;
        }

        var header = reference[5..comma];
        var mediaType = header.Split(';', 2)[0].ToLowerInvariant();
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            return null;
        }

        if (mediaType is "image/wmf" or "image/emf" or "image/x-wmf" or "image/x-emf")
        {
            warnings.Add("A WMF/EMF image was omitted because it cannot be rendered safely.");
            return null;
        }

        byte[] data;
        if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeBase64Image(
                    reference.AsSpan(comma + 1),
                    limits.MaximumResourceBytes,
                    out data,
                    out var tooLarge))
            {
                warnings.Add(
                    tooLarge
                        ? "An oversized embedded RTF image was omitted."
                        : "An invalid embedded RTF image was omitted.");
                return null;
            }
        }
        else
        {
            try
            {
                data = Encoding.UTF8.GetBytes(
                    Uri.UnescapeDataString(reference[(comma + 1)..]));
            }
            catch (FormatException)
            {
                warnings.Add("An invalid embedded RTF image was omitted.");
                return null;
            }
        }

        FormatUtilities.ValidateImage(data, mediaType, limits);
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var id = FormatUtilities.StableId("resource", hash);
        resources.TryAdd(id, new BookResource(id, mediaType, data));
        ImageDimensions.TryRead(data, mediaType, out var width, out var height);
        return new ResolvedImage(
            id,
            alternativeText,
            width > 0 ? width : null,
            height > 0 ? height : null);
    }

    internal static bool TryDecodeBase64Image(
        ReadOnlySpan<char> encoded,
        long maximumBytes,
        out byte[] data,
        out bool tooLarge)
    {
        data = [];
        tooLarge = false;
        long significantCharacters = 0;
        char last = '\0';
        char secondLast = '\0';
        foreach (var character in encoded)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            secondLast = last;
            last = character;
            significantCharacters++;
        }

        if (significantCharacters % 4 != 0)
        {
            return false;
        }

        var padding = last == '='
            ? secondLast == '=' ? 2 : 1
            : 0;
        var decodedLength = (significantCharacters / 4 * 3) - padding;
        if (decodedLength < 0)
        {
            return false;
        }

        if (decodedLength > maximumBytes || decodedLength > int.MaxValue)
        {
            tooLarge = true;
            return false;
        }

        data = GC.AllocateUninitializedArray<byte>((int)decodedLength);
        if (!Convert.TryFromBase64Chars(encoded, data, out var bytesWritten) ||
            bytesWritten != data.Length)
        {
            data = [];
            return false;
        }

        return true;
    }

    private static IEnumerable<BookResource> ExtractPictureGroups(
        string rtf,
        ImportSecurityLimits limits,
        ICollection<string> warnings)
    {
        var index = 0;
        foreach (Match match in PictureGroupRegex().Matches(rtf))
        {
            var body = match.Groups["body"].Value;
            var mediaType = body.Contains("\\pngblip", StringComparison.Ordinal)
                ? "image/png"
                : body.Contains("\\jpegblip", StringComparison.Ordinal)
                    ? "image/jpeg"
                    : body.Contains("\\emfblip", StringComparison.Ordinal)
                        ? "image/emf"
                        : body.Contains("\\wmetafile", StringComparison.Ordinal)
                            ? "image/wmf"
                            : null;
            if (mediaType is null)
            {
                continue;
            }

            if (mediaType is "image/wmf" or "image/emf")
            {
                warnings.Add("A WMF/EMF picture was omitted.");
                continue;
            }

            var hex = string.Concat(HexRunRegex()
                .Matches(body)
                .Select(value => value.Value));
            if (hex.Length < 4 || hex.Length % 2 != 0)
            {
                continue;
            }

            if (hex.Length / 2L > limits.MaximumResourceBytes)
            {
                warnings.Add("An oversized RTF picture was omitted.");
                continue;
            }

            byte[] data;
            try
            {
                data = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                continue;
            }

            if (FormatUtilities.DetectMediaType(data) is not { } detected ||
                detected != mediaType)
            {
                continue;
            }

            FormatUtilities.ValidateImage(data, mediaType, limits);
            yield return new BookResource(
                FormatUtilities.StableId(
                    "resource",
                    Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                    (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                mediaType,
                data);
        }
    }

    private static string DecodeRtfContainer(byte[] data)
    {
        var latin1 = Encoding.Latin1.GetString(data);
        var match = AnsiCodePageRegex().Match(latin1);
        if (match.Success &&
            int.TryParse(match.Groups["codepage"].Value, out var codePage))
        {
            try
            {
                return Encoding.GetEncoding(codePage).GetString(data);
            }
            catch (ArgumentException)
            {
            }
        }

        return Encoding.GetEncoding(1252).GetString(data);
    }

    private static string? ExtractInfo(string rtf, string name)
    {
        var match = Regex.Match(
            rtf,
            $@"\\{Regex.Escape(name)}\s+(?<value>[^{{}}]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var value = ControlWordRegex().Replace(match.Groups["value"].Value, string.Empty)
            .Replace("\\{", "{", StringComparison.Ordinal)
            .Replace("\\}", "}", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        var cleaned = FormatUtilities.CleanText(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"\\ansicpg(?<codepage>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AnsiCodePageRegex();

    [GeneratedRegex(@"\{\\pict(?<body>[^{}]*)\}", RegexOptions.Singleline)]
    private static partial Regex PictureGroupRegex();

    [GeneratedRegex(@"(?<![A-Za-z\\])[0-9A-Fa-f]{16,}")]
    private static partial Regex HexRunRegex();

    [GeneratedRegex(@"\\[A-Za-z]+-?\d*\s?")]
    private static partial Regex ControlWordRegex();
}
