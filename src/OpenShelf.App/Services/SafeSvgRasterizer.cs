using System.Globalization;
using System.Text;
using System.Xml;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace OpenShelf.App.Services;

/// <summary>
/// Converts book-supplied SVG into a bounded raster image after rejecting active
/// content and every reference that could escape the SVG payload.
/// </summary>
public static class SafeSvgRasterizer
{
    public const int MaximumSvgBytes = 4 * 1024 * 1024;
    public const int MaximumElementCount = 25_000;
    public const int MaximumXmlDepth = 64;
    public const int MaximumOutputDimension = 4_096;
    public const long MaximumOutputPixels = 8_388_608;
    public const int MaximumEmbeddedRasterBytes = 2 * 1024 * 1024;

    private const int MaximumIntrinsicDimension = 16_384;
    private const long MaximumIntrinsicPixels = 67_108_864;
    private const int PlaceholderWidth = 96;
    private const int PlaceholderHeight = 128;

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "foreignObject",
        "form",
        "input",
        "button",
        "select",
        "option",
        "textarea",
        "iframe",
        "object",
        "embed",
        "applet",
        "audio",
        "video",
        "canvas",
        "portal",
        "handler",
        "listener",
        "prefetch",
        "animate",
        "animateMotion",
        "animateTransform",
        "set",
        "discard"
    };

    private static readonly HashSet<string> AllowedRasterMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/gif",
        "image/webp",
        "image/bmp"
    };

    public static bool TryRasterize(
        ReadOnlyMemory<byte> svgBytes,
        out Bitmap? bitmap,
        out SvgRasterizationFailure failure,
        int maximumWidth = 1_600,
        int maximumHeight = 1_600,
        CancellationToken cancellationToken = default)
    {
        bitmap = null;
        failure = SvgRasterizationFailure.None;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumWidth <= 0 || maximumHeight <= 0 ||
                maximumWidth > MaximumOutputDimension || maximumHeight > MaximumOutputDimension ||
                (long)maximumWidth * maximumHeight > MaximumOutputPixels)
            {
                failure = SvgRasterizationFailure.LimitExceeded;
                return false;
            }

            if (svgBytes.Length > MaximumSvgBytes)
            {
                failure = SvgRasterizationFailure.LimitExceeded;
                return false;
            }

            // Snapshot caller-owned memory once so it cannot change between
            // validation and rendering.
            var trustedSnapshot = svgBytes.ToArray();
            failure = Validate(trustedSnapshot, cancellationToken);
            if (failure != SvgRasterizationFailure.None)
            {
                return false;
            }

            using var input = new MemoryStream(trustedSnapshot, writable: false);
            using var svg = new SKSvg();
            var picture = svg.Load(input);
            cancellationToken.ThrowIfCancellationRequested();
            if (picture is null || !TryGetSafeBounds(picture.CullRect, out var bounds))
            {
                failure = SvgRasterizationFailure.DecodeFailed;
                return false;
            }

            var scale = Math.Min(maximumWidth / bounds.Width, maximumHeight / bounds.Height);
            if (!float.IsFinite(scale) || scale <= 0)
            {
                failure = SvgRasterizationFailure.DecodeFailed;
                return false;
            }

            var outputWidth = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, maximumWidth);
            var outputHeight = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, maximumHeight);
            if ((long)outputWidth * outputHeight > MaximumOutputPixels)
            {
                failure = SvgRasterizationFailure.LimitExceeded;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var surface = SKSurface.Create(
                new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
            {
                failure = SvgRasterizationFailure.RenderFailed;
                return false;
            }

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Save();
            canvas.Scale(scale, scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Restore();
            canvas.Flush();

            cancellationToken.ThrowIfCancellationRequested();
            bitmap = SnapshotBitmap(surface);
            if (bitmap is null)
            {
                failure = SvgRasterizationFailure.RenderFailed;
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            bitmap?.Dispose();
            bitmap = null;
            failure = SvgRasterizationFailure.Cancelled;
            return false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            bitmap?.Dispose();
            bitmap = null;
            failure = SvgRasterizationFailure.RenderFailed;
            return false;
        }
    }

    public static Bitmap RasterizeOrPlaceholder(
        ReadOnlyMemory<byte> svgBytes,
        out SvgRasterizationFailure failure,
        int maximumWidth = 1_600,
        int maximumHeight = 1_600,
        CancellationToken cancellationToken = default)
    {
        return TryRasterize(
            svgBytes,
            out var bitmap,
            out failure,
            maximumWidth,
            maximumHeight,
            cancellationToken)
            ? bitmap!
            : CreatePlaceholder();
    }

    public static Bitmap CreatePlaceholder()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(PlaceholderWidth, PlaceholderHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Unable to create an SVG placeholder surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(45, 49, 59));

        using var framePaint = new SKPaint
        {
            Color = new SKColor(112, 120, 137),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };
        using var markPaint = new SKPaint
        {
            Color = new SKColor(192, 198, 210),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = 5
        };

        canvas.DrawRoundRect(new SKRect(16, 14, 80, 114), 7, 7, framePaint);
        canvas.DrawLine(32, 47, 64, 79, markPaint);
        canvas.DrawLine(64, 47, 32, 79, markPaint);
        canvas.Flush();
        return SnapshotBitmap(surface)
            ?? throw new InvalidOperationException("Unable to encode an SVG placeholder.");
    }

    private static SvgRasterizationFailure Validate(
        byte[] svgBytes,
        CancellationToken cancellationToken)
    {
        if (svgBytes.Length == 0)
        {
            return SvgRasterizationFailure.EmptyInput;
        }

        if (svgBytes.Length > MaximumSvgBytes)
        {
            return SvgRasterizationFailure.LimitExceeded;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumSvgBytes,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
            CloseInput = true
        };

        try
        {
            using var input = new MemoryStream(svgBytes, writable: false);
            using var reader = XmlReader.Create(input, settings);
            var elements = 0;
            var foundRoot = false;
            var styleDepth = -1;
            StringBuilder? styleText = null;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > MaximumXmlDepth)
                {
                    return SvgRasterizationFailure.LimitExceeded;
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        elements++;
                        if (elements > MaximumElementCount)
                        {
                            return SvgRasterizationFailure.LimitExceeded;
                        }

                        if (!foundRoot)
                        {
                            foundRoot = true;
                            if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                            {
                                return SvgRasterizationFailure.UnsafeContent;
                            }
                        }

                        if (ForbiddenElements.Contains(reader.LocalName))
                        {
                            return SvgRasterizationFailure.UnsafeContent;
                        }

                        if (reader.Depth == 0 && !ValidateIntrinsicDimensions(reader))
                        {
                            return SvgRasterizationFailure.LimitExceeded;
                        }

                        if (!ValidateAttributes(reader, cancellationToken))
                        {
                            return SvgRasterizationFailure.UnsafeContent;
                        }

                        if (reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                        {
                            styleDepth = reader.Depth;
                            styleText = new StringBuilder();
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (styleDepth >= 0)
                        {
                            styleText!.Append(reader.Value);
                            if (styleText.Length > MaximumSvgBytes ||
                                !ValidateCss(styleText.ToString(), allowIncompleteUrl: true, cancellationToken))
                            {
                                return SvgRasterizationFailure.UnsafeContent;
                            }
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (styleDepth == reader.Depth &&
                            reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!ValidateCss(styleText?.ToString() ?? string.Empty, allowIncompleteUrl: false, cancellationToken))
                            {
                                return SvgRasterizationFailure.UnsafeContent;
                            }

                            styleDepth = -1;
                            styleText = null;
                        }

                        break;

                    case XmlNodeType.DocumentType:
                    case XmlNodeType.Entity:
                    case XmlNodeType.EntityReference:
                    case XmlNodeType.ProcessingInstruction:
                        return SvgRasterizationFailure.UnsafeContent;
                }
            }

            return foundRoot ? SvgRasterizationFailure.None : SvgRasterizationFailure.InvalidXml;
        }
        catch (XmlException)
        {
            return SvgRasterizationFailure.InvalidXml;
        }
    }

    private static bool ValidateAttributes(XmlReader reader, CancellationToken cancellationToken)
    {
        if (!reader.HasAttributes)
        {
            return true;
        }

        while (reader.MoveToNextAttribute())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localName = reader.LocalName;
            var value = reader.Value.Trim();

            if (localName.Length > 2 && localName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                reader.MoveToElement();
                return false;
            }

            if (localName.Equals("base", StringComparison.OrdinalIgnoreCase) &&
                reader.NamespaceURI.Equals("http://www.w3.org/XML/1998/namespace", StringComparison.Ordinal))
            {
                reader.MoveToElement();
                return false;
            }

            if (localName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("poster", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateReference(value, cancellationToken))
                {
                    reader.MoveToElement();
                    return false;
                }
            }

            if (localName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateCss(value, allowIncompleteUrl: false, cancellationToken))
                {
                    reader.MoveToElement();
                    return false;
                }
            }
            else if (ContainsUrlFunction(value) &&
                     !ValidateCss(value, allowIncompleteUrl: false, cancellationToken))
            {
                reader.MoveToElement();
                return false;
            }

            var isNamespaceDeclaration =
                reader.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal);
            if (!isNamespaceDeclaration && ContainsDangerousScheme(value))
            {
                reader.MoveToElement();
                return false;
            }
        }

        reader.MoveToElement();
        return true;
    }

    private static bool ValidateIntrinsicDimensions(XmlReader reader)
    {
        var width = ParseSvgLength(reader.GetAttribute("width"));
        var height = ParseSvgLength(reader.GetAttribute("height"));
        if (!IsSafeDimension(width) || !IsSafeDimension(height))
        {
            return false;
        }

        if (width is > 0 && height is > 0 && width.Value * height.Value > MaximumIntrinsicPixels)
        {
            return false;
        }

        var viewBox = reader.GetAttribute("viewBox");
        if (string.IsNullOrWhiteSpace(viewBox))
        {
            return true;
        }

        var parts = viewBox.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewWidth) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var viewHeight))
        {
            return false;
        }

        return double.IsFinite(viewX) &&
               double.IsFinite(viewY) &&
               Math.Abs(viewX) <= MaximumIntrinsicDimension &&
               Math.Abs(viewY) <= MaximumIntrinsicDimension &&
               IsSafeDimension(viewWidth) &&
               IsSafeDimension(viewHeight) &&
               viewWidth * viewHeight <= MaximumIntrinsicPixels;
    }

    private static double? ParseSvgLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.TrimEnd().EndsWith('%'))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var end = 0;
        while (end < span.Length &&
               (char.IsDigit(span[end]) || span[end] is '+' or '-' or '.' or 'e' or 'E'))
        {
            end++;
        }

        return end > 0 && double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }

    private static bool IsSafeDimension(double? value) =>
        value is null || IsSafeDimension(value.Value);

    private static bool IsSafeDimension(double value) =>
        double.IsFinite(value) && value > 0 && value <= MaximumIntrinsicDimension;

    private static bool ValidateCss(
        string css,
        bool allowIncompleteUrl,
        CancellationToken cancellationToken)
    {
        if (css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("vbscript:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("expression(", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("behavior:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("-moz-binding", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.Contains('\\'))
        {
            return false;
        }

        if (!TryRemoveCssComments(css, allowIncompleteUrl, out var normalized))
        {
            return false;
        }

        var searchIndex = 0;
        while (searchIndex < normalized.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urlIndex = normalized.IndexOf("url", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (urlIndex < 0)
            {
                return true;
            }

            if (urlIndex > 0 && IsCssIdentifierCharacter(normalized[urlIndex - 1]))
            {
                searchIndex = urlIndex + 3;
                continue;
            }

            var cursor = urlIndex + 3;
            while (cursor < normalized.Length && char.IsWhiteSpace(normalized[cursor]))
            {
                cursor++;
            }

            if (cursor >= normalized.Length)
            {
                return allowIncompleteUrl;
            }

            if (normalized[cursor] != '(')
            {
                searchIndex = cursor;
                continue;
            }

            cursor++;
            var valueStart = cursor;
            char quote = '\0';
            while (cursor < normalized.Length)
            {
                var current = normalized[cursor];
                if (quote == '\0' && (current == '\'' || current == '"'))
                {
                    quote = current;
                }
                else if (quote != '\0' && current == quote)
                {
                    quote = '\0';
                }
                else if (quote == '\0' && current == ')')
                {
                    break;
                }

                cursor++;
            }

            if (cursor >= normalized.Length)
            {
                return allowIncompleteUrl;
            }

            var reference = normalized[valueStart..cursor].Trim();
            if (reference.Length >= 2 &&
                ((reference[0] == '\'' && reference[^1] == '\'') ||
                 (reference[0] == '"' && reference[^1] == '"')))
            {
                reference = reference[1..^1].Trim();
            }

            if (!ValidateReference(reference, cancellationToken))
            {
                return false;
            }

            searchIndex = cursor + 1;
        }

        return true;
    }

    private static bool TryRemoveCssComments(string css, bool allowIncomplete, out string normalized)
    {
        var builder = new StringBuilder(css.Length);
        for (var index = 0; index < css.Length; index++)
        {
            if (index + 1 < css.Length && css[index] == '/' && css[index + 1] == '*')
            {
                var end = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    normalized = builder.ToString();
                    return allowIncomplete;
                }

                builder.Append(' ');
                index = end + 1;
                continue;
            }

            builder.Append(css[index]);
        }

        normalized = builder.ToString();
        return true;
    }

    private static bool ValidateReference(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var normalized = reference.Trim();
        if (normalized[0] == '#')
        {
            return normalized.Length > 1 && !normalized.Any(char.IsControl);
        }

        if (!normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ValidateEmbeddedRaster(normalized, cancellationToken);
    }

    private static bool ValidateEmbeddedRaster(string dataUri, CancellationToken cancellationToken)
    {
        var comma = dataUri.IndexOf(',');
        if (comma <= 5)
        {
            return false;
        }

        var metadata = dataUri[5..comma];
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (metadataParts.Length < 2 ||
            !AllowedRasterMediaTypes.Contains(metadataParts[0]) ||
            !metadataParts.Skip(1).Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var encoded = dataUri[(comma + 1)..];
        if (encoded.Length == 0 || encoded.Length > ((MaximumEmbeddedRasterBytes + 2) / 3 * 4) + 16)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length == 0 || decoded.Length > MaximumEmbeddedRasterBytes)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(decoded);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return false;
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        return width > 0 && height > 0 &&
               width <= MaximumOutputDimension && height <= MaximumOutputDimension &&
               (long)width * height <= MaximumOutputPixels;
    }

    private static bool ContainsDangerousScheme(string value)
    {
        var candidate = value;
        for (var pass = 0; pass < 4; pass++)
        {
            if (candidate.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.IndexOf("vbscript:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.IndexOf("file:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.IndexOf("http:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.IndexOf("https:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.IndexOf("ftp:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidate.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }

            if (!candidate.Contains('%'))
            {
                return false;
            }

            try
            {
                var decoded = Uri.UnescapeDataString(candidate);
                if (decoded.Equals(candidate, StringComparison.Ordinal))
                {
                    return false;
                }

                candidate = decoded;
            }
            catch (UriFormatException)
            {
                return true;
            }
        }

        return candidate.Contains('%');
    }

    private static bool ContainsUrlFunction(string value) =>
        value.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsCssIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-';

    private static bool TryGetSafeBounds(SKRect candidate, out SKRect bounds)
    {
        bounds = candidate;
        return float.IsFinite(candidate.Left) &&
               float.IsFinite(candidate.Top) &&
               float.IsFinite(candidate.Right) &&
               float.IsFinite(candidate.Bottom) &&
               candidate.Width > 0 && candidate.Height > 0 &&
               candidate.Width <= MaximumIntrinsicDimension &&
               candidate.Height <= MaximumIntrinsicDimension &&
               candidate.Width * candidate.Height <= MaximumIntrinsicPixels;
    }

    private static Bitmap? SnapshotBitmap(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null)
        {
            return null;
        }

        using var stream = new MemoryStream(encoded.ToArray(), writable: false);
        return new Bitmap(stream);
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}

public enum SvgRasterizationFailure
{
    None,
    EmptyInput,
    InvalidXml,
    UnsafeContent,
    LimitExceeded,
    DecodeFailed,
    RenderFailed,
    Cancelled
}
