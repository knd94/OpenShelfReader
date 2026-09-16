using System.Collections.Immutable;
using System.Text;
using AngleSharp.Dom;

namespace OpenShelf.Formats;

internal sealed record CssBackgroundRule(
    CssSimpleSelector Selector,
    string BasePath,
    string ImageReference,
    int SourceOrder)
{
    public int Specificity => Selector.Specificity;

    public bool Matches(IElement element) => Selector.Matches(element);
}

internal sealed record CssSimpleSelector(
    string? ElementName,
    string? Id,
    ImmutableArray<string> Classes)
{
    public int Specificity =>
        (Id is null ? 0 : 100) + (Classes.Length * 10) +
        (ElementName is null ? 0 : 1);

    public bool Matches(IElement element)
    {
        if (ElementName is not null &&
            !element.TagName.Equals(ElementName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Id is not null &&
            !string.Equals(element.Id, Id, StringComparison.Ordinal))
        {
            return false;
        }

        return Classes.All(className => element.ClassList.Contains(className));
    }

    public static bool TryParse(string selector, out CssSimpleSelector result)
    {
        result = null!;
        var value = selector.AsSpan().Trim();
        if (value.IsEmpty)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return false;
            }
        }

        var cursor = 0;
        string? elementName = null;
        string? id = null;
        var classes = ImmutableArray.CreateBuilder<string>();
        if (value[cursor] is not '#' and not '.')
        {
            var start = cursor;
            while (cursor < value.Length && IsIdentifierCharacter(value[cursor]))
            {
                cursor++;
            }

            if (cursor == start)
            {
                return false;
            }

            elementName = value[start..cursor].ToString();
        }

        while (cursor < value.Length)
        {
            var prefix = value[cursor++];
            if (prefix is not '#' and not '.')
            {
                return false;
            }

            var start = cursor;
            while (cursor < value.Length && IsIdentifierCharacter(value[cursor]))
            {
                cursor++;
            }

            if (cursor == start)
            {
                return false;
            }

            var name = value[start..cursor].ToString();
            if (prefix == '#')
            {
                if (id is not null)
                {
                    return false;
                }

                id = name;
            }
            else
            {
                classes.Add(name);
            }
        }

        if (elementName is null && id is null && classes.Count == 0)
        {
            return false;
        }

        result = new CssSimpleSelector(elementName, id, classes.ToImmutable());
        return true;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ||
        character >= '\u0080';
}

internal static class CssBackgroundStylesheetParser
{
    private const int MaximumAcceptedRules = 4_096;

    public static ImmutableArray<CssBackgroundRule> Parse(
        string css,
        string basePath)
    {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var rules = ImmutableArray.CreateBuilder<CssBackgroundRule>();
        var cursor = 0;
        var sourceOrder = 0;
        while (cursor < css.Length)
        {
            SkipWhitespaceAndComments(css, ref cursor);
            if (cursor >= css.Length)
            {
                break;
            }

            if (css[cursor] == '@')
            {
                SkipAtRule(css, ref cursor);
                continue;
            }

            var openingBrace = FindNextStructuralCharacter(css, cursor, '{');
            if (openingBrace < 0)
            {
                break;
            }

            var closingBrace = FindMatchingBrace(css, openingBrace);
            if (closingBrace < 0)
            {
                break;
            }

            var selectors = StripComments(css[cursor..openingBrace]);
            var declarations = css.AsSpan(
                openingBrace + 1,
                closingBrace - openingBrace - 1);
            if (InlineCssImageParser.TryExtractBackgroundImage(
                    declarations.ToString(),
                    out var imageReference))
            {
                foreach (var selector in selectors.Split(','))
                {
                    if (!CssSimpleSelector.TryParse(selector, out var parsedSelector))
                    {
                        continue;
                    }

                    if (rules.Count >= MaximumAcceptedRules)
                    {
                        throw FormatImportException.Unsupported(
                            $"A stylesheet contains more than {MaximumAcceptedRules:N0} " +
                            "supported background-image rules.");
                    }

                    rules.Add(new CssBackgroundRule(
                        parsedSelector,
                        basePath,
                        imageReference,
                        sourceOrder++));
                }
            }

            cursor = closingBrace + 1;
        }

        return rules.ToImmutable();
    }

    private static void SkipAtRule(string css, ref int cursor)
    {
        var terminator = FindNextAtRuleTerminator(css, cursor);
        if (terminator < 0)
        {
            cursor = css.Length;
            return;
        }

        if (css[terminator] == ';')
        {
            cursor = terminator + 1;
            return;
        }

        var closingBrace = FindMatchingBrace(css, terminator);
        cursor = closingBrace < 0 ? css.Length : closingBrace + 1;
    }

    private static int FindNextAtRuleTerminator(string css, int start)
    {
        char quote = '\0';
        var parentheses = 0;
        for (var index = start; index < css.Length; index++)
        {
            if (SkipStringOrComment(css, ref index, ref quote))
            {
                continue;
            }

            if (quote != '\0')
            {
                continue;
            }

            if (css[index] == '(')
            {
                parentheses++;
            }
            else if (css[index] == ')' && parentheses > 0)
            {
                parentheses--;
            }
            else if (parentheses == 0 && css[index] is ';' or '{')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindNextStructuralCharacter(
        string css,
        int start,
        char expected)
    {
        char quote = '\0';
        for (var index = start; index < css.Length; index++)
        {
            if (SkipStringOrComment(css, ref index, ref quote))
            {
                continue;
            }

            if (quote == '\0' && css[index] == expected)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingBrace(string css, int openingBrace)
    {
        char quote = '\0';
        var depth = 0;
        for (var index = openingBrace; index < css.Length; index++)
        {
            if (SkipStringOrComment(css, ref index, ref quote))
            {
                continue;
            }

            if (quote != '\0')
            {
                continue;
            }

            if (css[index] == '{')
            {
                depth++;
            }
            else if (css[index] == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SkipStringOrComment(
        string css,
        ref int index,
        ref char quote)
    {
        var character = css[index];
        if (quote != '\0')
        {
            if (character == '\\' && index + 1 < css.Length)
            {
                index++;
            }
            else if (character == quote)
            {
                quote = '\0';
            }

            return true;
        }

        if (character is '\'' or '"')
        {
            quote = character;
            return true;
        }

        if (character == '/' &&
            index + 1 < css.Length &&
            css[index + 1] == '*')
        {
            var closing = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = closing < 0 ? css.Length - 1 : closing + 1;
            return true;
        }

        return false;
    }

    private static void SkipWhitespaceAndComments(string css, ref int cursor)
    {
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < css.Length &&
                css[cursor] == '/' &&
                css[cursor + 1] == '*')
            {
                var closing = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                cursor = closing < 0 ? css.Length : closing + 2;
                continue;
            }

            break;
        }
    }

    private static string StripComments(string value)
    {
        if (!value.Contains("/*", StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        while (cursor < value.Length)
        {
            var opening = value.IndexOf("/*", cursor, StringComparison.Ordinal);
            if (opening < 0)
            {
                builder.Append(value, cursor, value.Length - cursor);
                break;
            }

            builder.Append(value, cursor, opening - cursor);
            var closing = value.IndexOf("*/", opening + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                break;
            }

            cursor = closing + 2;
        }

        return builder.ToString();
    }
}
