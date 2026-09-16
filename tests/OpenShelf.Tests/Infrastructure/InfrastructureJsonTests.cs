using System.Collections.Immutable;
using OpenShelf.Core;
using OpenShelf.Infrastructure;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class InfrastructureJsonTests
{
    [Fact]
    public void ReflowableAnchorRoundTripsWithQuoteContext()
    {
        var anchor = new ReflowableAnchor(
            Guid.NewGuid(),
            "revision",
            "chapter-2",
            "paragraph-4",
            12,
            27,
            "selected text",
            "before",
            "after");

        var result = InfrastructureJson.DeserializeAnchor(
            InfrastructureJson.SerializeAnchor(anchor));

        Assert.Equal(anchor, Assert.IsType<ReflowableAnchor>(result));
    }

    [Fact]
    public void PdfAnchorRoundTripsRectangles()
    {
        var anchor = new PdfAnchor(
            Guid.NewGuid(),
            "revision",
            8,
            100,
            24,
            ImmutableArray.Create(
                new DocumentRectangle(10.5, 20.25, 30.75, 12.5)),
            "quoted",
            WithinPageOffset: 0.42);

        var result = InfrastructureJson.DeserializeAnchor(
            InfrastructureJson.SerializeAnchor(anchor));

        var pdf = Assert.IsType<PdfAnchor>(result);
        Assert.Equal(anchor.BookId, pdf.BookId);
        Assert.Equal(anchor.RevisionHash, pdf.RevisionHash);
        Assert.Equal(anchor.PageIndex, pdf.PageIndex);
        Assert.Equal(anchor.StartCharacter, pdf.StartCharacter);
        Assert.Equal(anchor.CharacterLength, pdf.CharacterLength);
        Assert.Equal(anchor.Rectangles.AsEnumerable(), pdf.Rectangles.AsEnumerable());
        Assert.Equal(anchor.ExactQuote, pdf.ExactQuote);
        Assert.Equal(anchor.WithinPageOffset, pdf.WithinPageOffset);
    }

    [Fact]
    public void LegacyPdfAnchorWithoutWithinPageOffsetUsesPageStart()
    {
        var json = """
            {"kind":"pdf","bookId":"00000000-0000-0000-0000-000000000001","revisionHash":"r","pageIndex":2,"startCharacter":0,"characterLength":0,"rectangles":[]}
            """;

        var pdf = Assert.IsType<PdfAnchor>(InfrastructureJson.DeserializeAnchor(json));

        Assert.Equal(0, pdf.WithinPageOffset);
    }

    [Fact]
    public void SettingsRoundTripAndEmptyPayloadUsesDefaults()
    {
        var settings = new ApplicationSettings(
            LibraryTheme.Light,
            ReaderPreferences.Default with
            {
                Theme = ReaderTheme.Sepia,
                FontFamily = "Atkinson Hyperlegible",
                FontSize = 23
            },
            "en-gb-x-rp",
            210,
            ConfirmExternalLinks: false);

        var result = InfrastructureJson.DeserializeSettings(
            InfrastructureJson.SerializeSettings(settings));

        Assert.Equal(settings, result);
        Assert.Equal(ApplicationSettings.Default, InfrastructureJson.DeserializeSettings(null));
    }

    [Fact]
    public void Legacy_settings_without_speech_tracking_preferences_opt_in_by_default()
    {
        const string legacyJson =
            """
            {"libraryTheme":"dark","reader":{"theme":"dark","layoutMode":"scroll","fontFamily":"Georgia","fontSize":20,"lineHeight":1.55,"paragraphSpacing":14,"horizontalMargin":48,"columnWidth":760,"alignment":"start"},"speechVoice":null,"speechRate":175,"confirmExternalLinks":true}
            """;

        var settings = InfrastructureJson.DeserializeSettings(legacyJson);

        Assert.True(settings.HighlightSpokenSentence);
        Assert.True(settings.FollowSpokenSentence);
        Assert.Equal(100, settings.SpeechVolume);
        Assert.Equal(1, settings.SpeechPitch);
    }
}
