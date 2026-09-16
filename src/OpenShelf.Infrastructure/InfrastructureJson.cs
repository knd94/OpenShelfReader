using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public static class InfrastructureJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string SerializeAnchor(DocumentAnchor? anchor) =>
        JsonSerializer.Serialize(anchor, Options);

    public static DocumentAnchor? DeserializeAnchor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DocumentAnchor>(json, Options)
            ?? throw new JsonException("The anchor payload was empty.");
    }

    public static string SerializeSettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, Options);
    }

    public static ApplicationSettings DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ApplicationSettings.Default;
        }

        var settings = JsonSerializer.Deserialize<ApplicationSettings>(json, Options)
            ?? ApplicationSettings.Default;

        // These preferences were added after the first settings payload shipped.
        // A missing JSON boolean normally deserializes as false, so explicitly
        // restore the intended opt-out defaults for existing installations.
        using var document = JsonDocument.Parse(json);
        return settings with
        {
            HighlightSpokenSentence = ReadOptionalBoolean(
                document.RootElement,
                "highlightSpokenSentence",
                defaultValue: true),
            FollowSpokenSentence = ReadOptionalBoolean(
                document.RootElement,
                "followSpokenSentence",
                defaultValue: true),
            SpeechVolume = ReadOptionalInt32(
                document.RootElement,
                "speechVolume",
                defaultValue: 100),
            SpeechPitch = ReadOptionalDouble(
                document.RootElement,
                "speechPitch",
                defaultValue: 1),
            PersonalReadingWordsPerMinute = ReadOptionalInt32(
                document.RootElement,
                "personalReadingWordsPerMinute",
                defaultValue: 250)
        };
    }

    internal static string SerializeMetadata(BookMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    internal static BookMetadata DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<BookMetadata>(json, Options)
        ?? throw new JsonException("The book metadata payload was empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DocumentAnchorJsonConverter());
        return options;
    }

    private static bool ReadOptionalBoolean(
        JsonElement element,
        string propertyName,
        bool defaultValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue
            };
        }

        return defaultValue;
    }

    private static int ReadOptionalInt32(
        JsonElement element,
        string propertyName,
        int defaultValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static double ReadOptionalDouble(
        JsonElement element,
        string propertyName,
        double defaultValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetDouble(out var value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private sealed class DocumentAnchorJsonConverter : JsonConverter<DocumentAnchor>
    {
        public override DocumentAnchor Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = GetRequiredString(root, "kind");
            var bookId = GetRequiredGuid(root, "bookId");
            var revisionHash = GetRequiredString(root, "revisionHash");

            return kind switch
            {
                "reflowable" => new ReflowableAnchor(
                    bookId,
                    revisionHash,
                    GetRequiredString(root, "sectionId"),
                    GetRequiredString(root, "blockId"),
                    GetRequiredInt32(root, "startOffset"),
                    GetRequiredInt32(root, "endOffset"),
                    GetOptionalString(root, "exactQuote"),
                    GetOptionalString(root, "prefix"),
                    GetOptionalString(root, "suffix")),
                "pdf" => new PdfAnchor(
                    bookId,
                    revisionHash,
                    GetRequiredInt32(root, "pageIndex"),
                    GetRequiredInt32(root, "startCharacter"),
                    GetRequiredInt32(root, "characterLength"),
                    ReadRectangles(root),
                    GetOptionalString(root, "exactQuote"),
                    GetOptionalDouble(root, "withinPageOffset")),
                _ => throw new JsonException($"Unsupported anchor kind '{kind}'.")
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            DocumentAnchor value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            switch (value)
            {
                case ReflowableAnchor reflowable:
                    writer.WriteString("kind", "reflowable");
                    WriteCommon(writer, reflowable);
                    writer.WriteString("sectionId", reflowable.SectionId);
                    writer.WriteString("blockId", reflowable.BlockId);
                    writer.WriteNumber("startOffset", reflowable.StartOffset);
                    writer.WriteNumber("endOffset", reflowable.EndOffset);
                    WriteOptionalString(writer, "exactQuote", reflowable.ExactQuote);
                    WriteOptionalString(writer, "prefix", reflowable.Prefix);
                    WriteOptionalString(writer, "suffix", reflowable.Suffix);
                    break;

                case PdfAnchor pdf:
                    writer.WriteString("kind", "pdf");
                    WriteCommon(writer, pdf);
                    writer.WriteNumber("pageIndex", pdf.PageIndex);
                    writer.WriteNumber("startCharacter", pdf.StartCharacter);
                    writer.WriteNumber("characterLength", pdf.CharacterLength);
                    writer.WriteNumber("withinPageOffset", pdf.WithinPageOffset);
                    writer.WritePropertyName("rectangles");
                    writer.WriteStartArray();
                    foreach (var rectangle in pdf.Rectangles)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("x", rectangle.X);
                        writer.WriteNumber("y", rectangle.Y);
                        writer.WriteNumber("width", rectangle.Width);
                        writer.WriteNumber("height", rectangle.Height);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    WriteOptionalString(writer, "exactQuote", pdf.ExactQuote);
                    break;

                default:
                    throw new JsonException(
                        $"Unsupported anchor type '{value.GetType().FullName}'.");
            }

            writer.WriteEndObject();
        }

        private static void WriteCommon(Utf8JsonWriter writer, DocumentAnchor anchor)
        {
            writer.WriteString("bookId", anchor.BookId);
            writer.WriteString("revisionHash", anchor.RevisionHash);
        }

        private static void WriteOptionalString(
            Utf8JsonWriter writer,
            string propertyName,
            string? value)
        {
            if (value is null)
            {
                writer.WriteNull(propertyName);
            }
            else
            {
                writer.WriteString(propertyName, value);
            }
        }

        private static string GetRequiredString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Anchor property '{propertyName}' is required.");
            }

            return property.GetString()
                ?? throw new JsonException($"Anchor property '{propertyName}' is required.");
        }

        private static string? GetOptionalString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : throw new JsonException($"Anchor property '{propertyName}' must be a string.");
        }

        private static Guid GetRequiredGuid(JsonElement element, string propertyName) =>
            Guid.TryParse(GetRequiredString(element, propertyName), out var value)
                ? value
                : throw new JsonException($"Anchor property '{propertyName}' is not a GUID.");

        private static int GetRequiredInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || !property.TryGetInt32(out var value))
            {
                throw new JsonException($"Anchor property '{propertyName}' must be an integer.");
            }

            return value;
        }

        private static double GetOptionalDouble(
            JsonElement element,
            string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.TryGetDouble(out var value)
                ? value
                : 0;
        }

        private static ImmutableArray<DocumentRectangle> ReadRectangles(JsonElement element)
        {
            if (!element.TryGetProperty("rectangles", out var rectangles)
                || rectangles.ValueKind != JsonValueKind.Array)
            {
                return ImmutableArray<DocumentRectangle>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<DocumentRectangle>();
            foreach (var rectangle in rectangles.EnumerateArray())
            {
                builder.Add(new DocumentRectangle(
                    GetRequiredDouble(rectangle, "x"),
                    GetRequiredDouble(rectangle, "y"),
                    GetRequiredDouble(rectangle, "width"),
                    GetRequiredDouble(rectangle, "height")));
            }

            return builder.ToImmutable();
        }

        private static double GetRequiredDouble(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || !property.TryGetDouble(out var value))
            {
                throw new JsonException($"Rectangle property '{propertyName}' must be a number.");
            }

            return value;
        }
    }
}
