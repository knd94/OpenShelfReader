using System;
using System.Globalization;

namespace OpenShelf.App.Services;

internal static class ReaderPositionLocator
{
    private const string PdfPrefix = "page:";

    public static bool TryParsePdf(
        string? locator,
        out int pageIndex,
        out double withinPage)
    {
        pageIndex = 0;
        withinPage = 0;
        if (string.IsNullOrWhiteSpace(locator)
            || !locator.StartsWith(PdfPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = locator[PdfPrefix.Length..];
        var separator = value.IndexOf('@');
        var pageText = separator >= 0 ? value[..separator] : value;
        if (!int.TryParse(
                pageText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out pageIndex)
            || pageIndex < 0)
        {
            pageIndex = 0;
            return false;
        }

        if (separator < 0)
        {
            return true;
        }

        var withinText = value[(separator + 1)..];
        if (withinText.Length == 0
            || !double.TryParse(
                withinText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out withinPage)
            || !double.IsFinite(withinPage))
        {
            pageIndex = 0;
            withinPage = 0;
            return false;
        }

        withinPage = Math.Clamp(withinPage, 0, 1);
        return true;
    }

    public static string FormatPdf(int pageIndex, double withinPage)
    {
        return FormattableString.Invariant(
            $"{PdfPrefix}{Math.Max(0, pageIndex)}@{Math.Clamp(withinPage, 0, 1):F4}");
    }

    public static bool TryParseReflowable(
        string? locator,
        out string baseLocator,
        out int characterOffset)
    {
        baseLocator = locator?.Trim() ?? string.Empty;
        characterOffset = 0;
        if (baseLocator.Length == 0 || baseLocator.StartsWith(PdfPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = baseLocator.LastIndexOf('#');
        if (separator < 0)
        {
            return true;
        }

        var offsetText = baseLocator[(separator + 1)..];
        if (separator == 0
            || !int.TryParse(
                offsetText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out characterOffset)
            || characterOffset < 0)
        {
            baseLocator = string.Empty;
            characterOffset = 0;
            return false;
        }

        baseLocator = baseLocator[..separator];
        return true;
    }

    public static string FormatReflowable(string baseLocator, int characterOffset)
    {
        if (!TryParseReflowable(baseLocator, out var normalized, out _))
        {
            normalized = baseLocator;
        }

        return $"{normalized}#{Math.Max(0, characterOffset)}";
    }

    public static string GetBaseLocator(string? locator)
    {
        if (TryParsePdf(locator, out var pageIndex, out _))
        {
            return $"{PdfPrefix}{pageIndex}";
        }

        return TryParseReflowable(locator, out var baseLocator, out _)
            ? baseLocator
            : locator?.Trim() ?? string.Empty;
    }
}
