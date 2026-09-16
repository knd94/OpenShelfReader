using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenShelf.Core;
using OpenShelf.Formats;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using PdfPoint = UglyToad.PdfPig.Core.PdfPoint;

namespace OpenShelf.Tests.Formats;

public sealed class FormatAdapterTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void RegistryContainsExactlyTheEightSupportedFormats()
    {
        var registry = new BookFormatRegistry();

        Assert.Equal(8, registry.Adapters.Count);
        Assert.Equal(
            Enum.GetValues<BookFormat>().Order(),
            registry.Adapters.Select(adapter => adapter.Format).Order());
        Assert.True(registry.TryResolve("book.epub", "PK\u0003\u0004"u8, out var epub));
        Assert.Equal(BookFormat.Epub, epub!.Format);
        Assert.True(registry.TryResolve("book.docx", "PK\u0003\u0004"u8, out var docx));
        Assert.Equal(BookFormat.Docx, docx!.Format);
        Assert.True(registry.TryResolve("book.pdf", "%PDF-1.7"u8, out var pdf));
        Assert.Equal(BookFormat.Pdf, pdf!.Format);
    }

    [Fact]
    public async Task TxtImportsHeadingsAndUnicodeDeterministically()
    {
        using var fixture = new TemporaryBook(".txt");
        await File.WriteAllTextAsync(
            fixture.Path,
            "CHAPTER ONE\n\nA café in London.\nSecond line.",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            TestContext.Current.CancellationToken);
        var adapter = new TxtFormatAdapter();

        var first = await adapter.ImportAsync(
            Request(fixture.Path, BookFormat.Txt),
            null,
            TestContext.Current.CancellationToken);
        var second = await adapter.ImportAsync(
            Request(fixture.Path, BookFormat.Txt),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(first.Document);
        Assert.Equal(first.Document.RevisionHash, second.Document.RevisionHash);
        Assert.Equal(
            document.Sections[0].Blocks.Select(block => block.Id),
            Assert.IsType<ReflowableDocument>(second.Document).Sections[0].Blocks.Select(block => block.Id));
        Assert.Contains("café", Flatten(document));
        Assert.IsType<HeadingBlock>(document.Sections[0].Blocks[0]);
    }

    [Fact]
    public async Task EpubImportsMetadataCoverInlineImageAndToc()
    {
        using var fixture = new TemporaryBook(".epub");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddText(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            AddText(
                archive,
                "OEBPS/content.opf",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Fixture EPUB</dc:title>
                    <dc:creator>Test Author</dc:creator>
                    <dc:identifier id="id">fixture-id</dc:identifier>
                  </metadata>
                  <manifest>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                    <item id="cover" href="images/cover.png" media-type="image/png" properties="cover-image"/>
                  </manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            AddText(
                archive,
                "OEBPS/chapter.xhtml",
                """
                <!doctype html>
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <h1 id="chapter-one">Chapter One</h1>
                  <p>Hello <strong>reader</strong>.</p>
                  <img src="images/cover.png" alt="Red cover"/>
                </body></html>
                """);
            AddBytes(archive, "OEBPS/images/cover.png", OnePixelPng);
        }

        var imported = await new EpubFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Epub),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal("Fixture EPUB", imported.Metadata.Title);
        Assert.Equal("Test Author", imported.Metadata.Author);
        Assert.NotNull(imported.CoverImage);
        Assert.Single(document.Resources);
        Assert.Contains(document.Sections[0].Blocks, block =>
            block is ParagraphBlock paragraph &&
            paragraph.Content.Any(content => content is InlineImage));
        Assert.NotEmpty(document.TableOfContents);
    }

    [Fact]
    public async Task EpubResolvesInlineCssBackgroundImagesAndBlocksUnsafeReferences()
    {
        using var fixture = new TemporaryBook(".epub");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddText(
                archive,
                "META-INF/container.xml",
                """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OPS/package/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            AddText(
                archive,
                "OPS/package/content.opf",
                """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>CSS image paths</dc:title>
                    <dc:creator>OpenShelf Tests</dc:creator>
                  </metadata>
                  <manifest>
                    <item id="chapter" href="../Text/chapters/chapter.xhtml" media-type="application/xhtml+xml"/>
                    <item id="cover-art" href="../Images/Cover Art.png" media-type="image/png"/>
                    <item id="nested" href="../Text/assets/nested.png" media-type="image/png"/>
                  </manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            AddText(
                archive,
                "OPS/Text/chapters/chapter.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <div style="background-image: url('../../Images/Cover%20Art.png')" aria-label="Quoted background"></div>
                  <p><span style="background-image: URL( ../assets/nested.png )" title="Unquoted background"></span>After image.</p>
                  <div style="background-image:url(https://example.invalid/tracker.png)" aria-label="Remote image blocked"></div>
                  <div style="background-image:url('../../../../outside.png')" aria-label="Traversal image blocked"></div>
                  <div style="background-image:url('../assets/missing.png')" aria-label="Missing image placeholder"></div>
                  <p><img src="../assets/also-missing.png"/>End.</p>
                </body></html>
                """);
            AddBytes(archive, "OPS/Images/Cover Art.png", OnePixelPng);
            AddBytes(archive, "OPS/Text/assets/nested.png", OnePixelPng);
        }

        var imported = await new EpubFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Epub),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal(2, document.Resources.Count);
        var blocks = Assert.Single(document.Sections).Blocks;
        var background = Assert.Single(blocks.OfType<ImageBlock>());
        Assert.Equal("Quoted background", background.AlternativeText);
        var inlineImage = Assert.Single(blocks
            .OfType<ParagraphBlock>()
            .SelectMany(paragraph => paragraph.Content)
            .OfType<InlineImage>());
        Assert.Equal("Unquoted background", inlineImage.AlternativeText);

        var readableText = Flatten(document);
        Assert.Contains("Remote image blocked", readableText, StringComparison.Ordinal);
        Assert.Contains("Traversal image blocked", readableText, StringComparison.Ordinal);
        Assert.Contains("Missing image placeholder", readableText, StringComparison.Ordinal);
        Assert.Contains("[Image unavailable]", readableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpubResolvesLocalAndLinkedStylesheetBackgroundsSafely()
    {
        using var fixture = new TemporaryBook(".epub");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddText(
                archive,
                "META-INF/container.xml",
                """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OPS/package/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            AddText(
                archive,
                "OPS/package/content.opf",
                """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Stylesheet backgrounds</dc:title>
                    <dc:creator>OpenShelf Tests</dc:creator>
                  </metadata>
                  <manifest>
                    <item id="chapter" href="../Text/chapters/chapter.xhtml" media-type="application/xhtml+xml"/>
                    <item id="styles" href="../Styles/theme one.css" media-type="text/css"/>
                    <item id="cover-art" href="../Images/Cover Art.png" media-type="image/png"/>
                    <item id="nested" href="../Text/assets/nested.png" media-type="image/png"/>
                  </manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            AddText(
                archive,
                "OPS/Styles/theme one.css",
                """
                @import url("https://example.invalid/import.css");
                div.cover { background-image: url('../Images/Cover%20Art.png'); }
                #nested { background: center / contain no-repeat url('../Text/assets/nested.png'); }
                aside { background-image: url('../Images/Cover%20Art.png'); }
                footer { background-image: url('../Images/Cover Art.png'); }
                .remote { background-image: url(data:image/png;base64,AAAA); }
                .escape { background-image: url('../../../outside.png'); }
                body .complex { background-image: url('../Images/Cover%20Art.png'); }
                """);
            AddText(
                archive,
                "OPS/Text/chapters/chapter.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <head>
                    <link rel="stylesheet" href="../../Styles/theme%20one.css"/>
                    <link rel="stylesheet" href="https://example.invalid/remote.css"/>
                    <link rel="stylesheet" href="../../../../outside.css"/>
                    <style type="text/css">
                      .local-card { background-image: url('../assets/nested.png'); }
                      #inline-wins { background-image: url('../assets/nested.png'); }
                      @import url('../../Styles/ignored.css');
                    </style>
                  </head>
                  <body>
                    <div class="cover" aria-label="Linked class background"></div>
                    <aside id="nested" aria-label="Linked id background"></aside>
                    <footer aria-label="Linked element background"></footer>
                    <section class="local-card" aria-label="Local style background"></section>
                    <div id="inline-wins" style="background-image:url('../../Images/Cover%20Art.png')" aria-label="Inline cascade winner"></div>
                    <div class="remote" aria-label="Remote CSS image blocked"></div>
                    <div class="escape" aria-label="Stylesheet traversal blocked"></div>
                    <div class="complex">Complex selector remains readable.</div>
                  </body>
                </html>
                """);
            AddBytes(archive, "OPS/Images/Cover Art.png", OnePixelPng);
            AddBytes(archive, "OPS/Text/assets/nested.png", OnePixelPng);
        }

        var imported = await new EpubFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Epub),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal(2, document.Resources.Count);
        var images = Assert.Single(document.Sections).Blocks
            .OfType<ImageBlock>()
            .ToDictionary(image => image.AlternativeText!, StringComparer.Ordinal);
        Assert.Equal(5, images.Count);
        Assert.Equal(
            images["Linked class background"].ResourceId,
            images["Linked element background"].ResourceId);
        Assert.Equal(
            images["Linked class background"].ResourceId,
            images["Inline cascade winner"].ResourceId);
        Assert.Equal(
            images["Linked id background"].ResourceId,
            images["Local style background"].ResourceId);
        Assert.NotEqual(
            images["Linked class background"].ResourceId,
            images["Linked id background"].ResourceId);

        var readableText = Flatten(document);
        Assert.Contains("Remote CSS image blocked", readableText, StringComparison.Ordinal);
        Assert.Contains("Stylesheet traversal blocked", readableText, StringComparison.Ordinal);
        Assert.Contains("Complex selector remains readable", readableText, StringComparison.Ordinal);
    }

    [Fact]
    public void StylesheetParserAcceptsOnlySimpleSelectorsAndSkipsImports()
    {
        var rules = CssBackgroundStylesheetParser.Parse(
            """
            @import url('https://example.invalid/import.css');
            div.card, #hero, .one.two { background-image: url('../Images/art%20one.png'); }
            body .descendant { background-image: url('../Images/ignored.png'); }
            [data-cover] { background-image: url('../Images/ignored.png'); }
            """,
            "OPS/Styles/book.css");

        Assert.Equal(3, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.Equal("OPS/Styles/book.css", rule.BasePath);
            Assert.Equal("../Images/art%20one.png", rule.ImageReference);
        });
        Assert.Contains(rules, rule => rule.Selector.ElementName == "div");
        Assert.Contains(rules, rule => rule.Selector.Id == "hero");
        Assert.Contains(rules, rule => rule.Selector.Classes.Length == 2);
    }

    [Theory]
    [InlineData("background-image:url(images/cover%20art.png)", "images/cover%20art.png")]
    [InlineData("background-image: url('images/cover art.png')", "images/cover art.png")]
    [InlineData("BACKGROUND-IMAGE: URL( \"../nested/cover.png\" )", "../nested/cover.png")]
    [InlineData("color:red; background-image: url(images/one\\ two.png);", "images/one two.png")]
    public void InlineCssBackgroundParserAcceptsPublisherUrlForms(
        string style,
        string expected)
    {
        Assert.True(InlineCssImageParser.TryExtractBackgroundImage(style, out var reference));
        Assert.Equal(expected, reference);
    }

    [Theory]
    [InlineData("OPS/Text/chapter.xhtml", "https://example.invalid/cover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "//example.invalid/cover.png")]
    [InlineData("OPS/Text/chapter.xhtml", "../../../outside.png")]
    [InlineData("OPS/Text/chapter.xhtml", "..%2f..%2f..%2foutside.png")]
    [InlineData("OPS/Text/chapter.xhtml", "..\\Images\\cover.png")]
    public void ArchivePathBlocksRemoteAndTraversalReferences(
        string basePath,
        string reference)
    {
        Assert.False(ArchivePath.TryResolve(basePath, reference, out _));
    }

    [Fact]
    public async Task EpubRejectsImagesExceedingTheAggregateResourceLimit()
    {
        using var fixture = new TemporaryBook(".epub");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddText(
                archive,
                "META-INF/container.xml",
                """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="content.opf"/></rootfiles>
                </container>
                """);
            AddText(
                archive,
                "content.opf",
                """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Aggregate images</dc:title>
                  </metadata>
                  <manifest>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                    <item id="first" href="first.png" media-type="image/png"/>
                    <item id="second" href="second.png" media-type="image/png"/>
                  </manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            AddText(
                archive,
                "chapter.xhtml",
                """<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Text</p></body></html>""");
            AddBytes(archive, "first.png", OnePixelPng);
            AddBytes(archive, "second.png", OnePixelPng);
        }

        var request = Request(fixture.Path, BookFormat.Epub);
        request = request with
        {
            Limits = request.Limits with
            {
                MaximumResourceBytes = OnePixelPng.LongLength + 1
            }
        };

        var exception = await Assert.ThrowsAsync<FormatImportException>(
            () => new EpubFormatAdapter().ImportAsync(
                request,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal(ImportOutcome.Unsupported, exception.Outcome);
        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fb2ImportsNestedContentAndBase64Images()
    {
        using var fixture = new TemporaryBook(".fb2");
        var image = Convert.ToBase64String(OnePixelPng);
        await File.WriteAllTextAsync(
            fixture.Path,
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"
                         xmlns:l="http://www.w3.org/1999/xlink">
              <description><title-info>
                <book-title>Fixture FB2</book-title>
                <author><first-name>Ada</first-name><last-name>Lovelace</last-name></author>
                <coverpage><image l:href="#cover"/></coverpage>
              </title-info></description>
              <body><section id="one"><title><p>One</p></title>
                <p>Hello <strong>FB2</strong>.</p><image l:href="#cover"/>
              </section></body>
              <binary id="cover" content-type="image/png">{{image}}</binary>
            </FictionBook>
            """,
            TestContext.Current.CancellationToken);

        var imported = await new Fb2FormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Fb2),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal("Fixture FB2", imported.Metadata.Title);
        Assert.Equal("Ada Lovelace", imported.Metadata.Author);
        Assert.NotNull(imported.CoverImage);
        Assert.Single(document.Resources);
        Assert.NotEmpty(document.TableOfContents);
        Assert.Contains("Hello FB2", Flatten(document));
    }

    [Fact]
    public async Task ManagedMobiFallbackImportsUncompressedTextAndImage()
    {
        using var fixture = new TemporaryBook(".mobi");
        await File.WriteAllBytesAsync(
            fixture.Path,
            CreateUncompressedMobiFixture(),
            TestContext.Current.CancellationToken);

        var imported = await new MobiFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Mobi),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Contains("Hello MOBI", Flatten(document));
        Assert.NotEmpty(document.Resources);
        Assert.NotNull(imported.CoverImage);
    }

    [Fact]
    public async Task RtfImportsFormattedText()
    {
        using var fixture = new TemporaryBook(".rtf");
        await File.WriteAllTextAsync(
            fixture.Path,
            """{\rtf1\ansi\ansicpg1252{\info{\title Fixture RTF}{\author Grace Hopper}}\b Heading\b0\par Hello \i reader\i0.\par}""",
            Encoding.ASCII,
            TestContext.Current.CancellationToken);

        var imported = await new RtfFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Rtf),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal("Fixture RTF", imported.Metadata.Title);
        Assert.Equal("Grace Hopper", imported.Metadata.Author);
        Assert.Contains("Hello reader", Flatten(document));
    }

    [Fact]
    public void RtfBase64PreflightRejectsOversizedPayloadBeforeAllocatingIt()
    {
        var encoded = Convert.ToBase64String(new byte[64]);

        var decoded = RtfFormatAdapter.TryDecodeBase64Image(
            encoded,
            maximumBytes: 16,
            out var data,
            out var tooLarge);

        Assert.False(decoded);
        Assert.True(tooLarge);
        Assert.Empty(data);
    }

    [Fact]
    public async Task DocxImportsMetadataHeadingsAndParagraphs()
    {
        using var fixture = new TemporaryBook(".docx");
        using (var document = WordprocessingDocument.Create(
                   fixture.Path,
                   WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(
                new Body(
                    new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                        new Run(new Text("Chapter One"))),
                    new Paragraph(new Run(new Text("Hello DOCX")))));
            document.PackageProperties.Title = "Fixture DOCX";
            document.PackageProperties.Creator = "Katherine Johnson";
            main.Document.Save();
        }

        var imported = await new DocxFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Docx),
            null,
            TestContext.Current.CancellationToken);

        var normalized = Assert.IsType<ReflowableDocument>(imported.Document);
        Assert.Equal("Fixture DOCX", imported.Metadata.Title);
        Assert.Equal("Katherine Johnson", imported.Metadata.Author);
        Assert.Contains("Hello DOCX", Flatten(normalized));
        Assert.NotEmpty(normalized.TableOfContents);
    }

    [Fact]
    public async Task DocxPreflightRejectsArchiveTraversal()
    {
        using var fixture = new TemporaryBook(".docx");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "../escape.xml", "<escape/>");
        }

        var exception = await Assert.ThrowsAsync<FormatImportException>(
            () => new DocxFormatAdapter().ImportAsync(
                Request(fixture.Path, BookFormat.Docx),
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal(ImportOutcome.Corrupt, exception.Outcome);
        Assert.Contains("Unsafe archive entry path", exception.Message);
    }

    [Fact]
    public async Task DocxPreflightEnforcesArchiveEntryCount()
    {
        using var fixture = new TemporaryBook(".docx");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddText(archive, "first.xml", "<first/>");
            AddText(archive, "second.xml", "<second/>");
        }

        var request = Request(fixture.Path, BookFormat.Docx);
        request = request with
        {
            Limits = request.Limits with { MaximumArchiveEntries = 1 }
        };
        var exception = await Assert.ThrowsAsync<FormatImportException>(
            () => new DocxFormatAdapter().ImportAsync(
                request,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal(ImportOutcome.Unsupported, exception.Outcome);
        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DocxPreflightEnforcesExpandedSize()
    {
        using var fixture = new TemporaryBook(".docx");
        using (var archive = ZipFile.Open(fixture.Path, ZipArchiveMode.Create))
        {
            AddBytes(archive, "first.bin", new byte[16]);
            AddBytes(archive, "second.bin", new byte[16]);
        }

        var request = Request(fixture.Path, BookFormat.Docx);
        request = request with
        {
            Limits = request.Limits with { MaximumExpandedBytes = 31 }
        };
        var exception = await Assert.ThrowsAsync<FormatImportException>(
            () => new DocxFormatAdapter().ImportAsync(
                request,
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal(ImportOutcome.Unsupported, exception.Outcome);
        Assert.Contains("expanded archive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebpVariantsEnforceDecodedPixelLimits()
    {
        var limits = ImportSecurityLimits.Default with
        {
            MaximumDecodedPixels = 1_000_000
        };

        foreach (var chunkType in new[] { "VP8X", "VP8 ", "VP8L" })
        {
            var image = CreateWebpFixture(chunkType, width: 2_000, height: 2_000);

            Assert.True(ImageDimensions.TryRead(
                image,
                "image/webp",
                out var width,
                out var height));
            Assert.Equal(2_000, width);
            Assert.Equal(2_000, height);
            var exception = Assert.Throws<FormatImportException>(
                () => FormatUtilities.ValidateImage(image, "image/webp", limits));
            Assert.Equal(ImportOutcome.Unsupported, exception.Outcome);
        }
    }

    [Fact]
    public void WebpValidationRejectsMalformedHeaders()
    {
        var image = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP");

        var exception = Assert.Throws<FormatImportException>(
            () => FormatUtilities.ValidateImage(
                image,
                "image/webp",
                ImportSecurityLimits.Default));

        Assert.Equal(ImportOutcome.Corrupt, exception.Outcome);
    }

    [Fact]
    public void SvgValidationEnforcesPayloadDimensionsAndSafeXml()
    {
        var oversizedDimensions = Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" width="2000" height="2000"/>""");
        var pixelLimits = ImportSecurityLimits.Default with
        {
            MaximumDecodedPixels = 1_000_000
        };
        var pixels = Assert.Throws<FormatImportException>(
            () => FormatUtilities.ValidateImage(
                oversizedDimensions,
                "image/svg+xml",
                pixelLimits));
        Assert.Equal(ImportOutcome.Unsupported, pixels.Outcome);

        var payloadLimits = ImportSecurityLimits.Default with
        {
            MaximumResourceBytes = oversizedDimensions.LongLength - 1
        };
        var payload = Assert.Throws<FormatImportException>(
            () => FormatUtilities.ValidateImage(
                oversizedDimensions,
                "image/svg+xml",
                payloadLimits));
        Assert.Equal(ImportOutcome.Unsupported, payload.Outcome);

        var entity = Encoding.UTF8.GetBytes(
            """<!DOCTYPE svg [<!ENTITY value "unsafe">]><svg xmlns="http://www.w3.org/2000/svg">&value;</svg>""");
        var xml = Assert.Throws<FormatImportException>(
            () => FormatUtilities.ValidateImage(
                entity,
                "image/svg+xml",
                ImportSecurityLimits.Default));
        Assert.Equal(ImportOutcome.Corrupt, xml.Outcome);
    }

    [Fact]
    public async Task PdfImportsTextAndGlyphGeometry()
    {
        using var fixture = new TemporaryBook(".pdf");
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PdfPageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Hello PDF", 12, new PdfPoint(72, 700), font);
        await File.WriteAllBytesAsync(
            fixture.Path,
            builder.Build(),
            TestContext.Current.CancellationToken);

        var imported = await new PdfFormatAdapter().ImportAsync(
            Request(fixture.Path, BookFormat.Pdf),
            null,
            TestContext.Current.CancellationToken);

        var document = Assert.IsType<FixedPageDocument>(imported.Document);
        Assert.Single(document.Pages);
        Assert.Contains("Hello PDF", document.Pages[0].PlainText);
        Assert.NotEmpty(document.Pages[0].Glyphs);
        Assert.All(document.Pages[0].Glyphs, glyph => Assert.True(glyph.Right >= glyph.Left));
    }

    private static BookImportRequest Request(string path, BookFormat format) =>
        new(
            path,
            Path.GetFileName(path),
            format,
            string.Empty,
            null,
            ImportSecurityLimits.Default with
            {
                MaximumInputBytes = 16 * 1024 * 1024,
                MaximumExpandedBytes = 64 * 1024 * 1024,
                MaximumResourceBytes = 8 * 1024 * 1024
            });

    private static string Flatten(ReflowableDocument document)
    {
        var builder = new StringBuilder();
        foreach (var block in document.Sections.SelectMany(section => section.Blocks))
        {
            AppendBlock(block, builder);
        }

        return builder.ToString();
    }

    private static void AppendBlock(DocumentBlock block, StringBuilder builder)
    {
        switch (block)
        {
            case HeadingBlock heading:
                builder.AppendLine(DocumentText.Flatten(heading.Content));
                break;
            case ParagraphBlock paragraph:
                builder.AppendLine(DocumentText.Flatten(paragraph.Content));
                break;
            case QuoteBlock quote:
                foreach (var child in quote.Blocks)
                {
                    AppendBlock(child, builder);
                }

                break;
            case ListBlock list:
                foreach (var item in list.Items)
                {
                    foreach (var child in item.Blocks)
                    {
                        AppendBlock(child, builder);
                    }
                }

                break;
        }
    }

    private static void AddText(
        ZipArchive archive,
        string path,
        string text,
        CompressionLevel compression = CompressionLevel.Optimal) =>
        AddBytes(archive, path, new UTF8Encoding(false).GetBytes(text), compression);

    private static void AddBytes(
        ZipArchive archive,
        string path,
        byte[] data,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compression);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static byte[] CreateWebpFixture(string chunkType, int width, int height)
    {
        byte[] payload;
        switch (chunkType)
        {
            case "VP8X":
                payload = new byte[10];
                var encodedWidth = width - 1;
                var encodedHeight = height - 1;
                payload[4] = (byte)encodedWidth;
                payload[5] = (byte)(encodedWidth >> 8);
                payload[6] = (byte)(encodedWidth >> 16);
                payload[7] = (byte)encodedHeight;
                payload[8] = (byte)(encodedHeight >> 8);
                payload[9] = (byte)(encodedHeight >> 16);
                break;
            case "VP8 ":
                payload = new byte[10];
                payload[3] = 0x9d;
                payload[4] = 0x01;
                payload[5] = 0x2a;
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)height);
                break;
            case "VP8L":
                payload = new byte[5];
                payload[0] = 0x2f;
                var sizeBits = (uint)(width - 1) | ((uint)(height - 1) << 14);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), sizeBits);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(chunkType));
        }

        var paddedPayloadLength = payload.Length + (payload.Length & 1);
        var result = new byte[20 + paddedPayloadLength];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(4, 4),
            (uint)(result.Length - 8));
        "WEBP"u8.CopyTo(result.AsSpan(8, 4));
        Encoding.ASCII.GetBytes(chunkType).CopyTo(result, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(16, 4),
            (uint)payload.Length);
        payload.CopyTo(result, 20);
        return result;
    }

    private static byte[] CreateUncompressedMobiFixture()
    {
        var markup = Encoding.UTF8.GetBytes(
            """<html><body><h1>Fixture</h1><p>Hello MOBI <img recindex="1" alt="cover"/></p></body></html>""");
        const int recordCount = 3;
        var recordZeroOffset = 78 + recordCount * 8;
        const int recordZeroLength = 248;
        var textOffset = recordZeroOffset + recordZeroLength;
        var imageOffset = textOffset + markup.Length;
        var result = new byte[imageOffset + OnePixelPng.Length];
        Encoding.ASCII.GetBytes("Fixture").CopyTo(result, 0);
        Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(result, 60);
        WriteBigEndian(result, 76, (ushort)recordCount);
        WriteBigEndian(result, 78, (uint)recordZeroOffset);
        WriteBigEndian(result, 86, (uint)textOffset);
        WriteBigEndian(result, 94, (uint)imageOffset);

        WriteBigEndian(result, recordZeroOffset, (ushort)1);
        WriteBigEndian(result, recordZeroOffset + 4, (uint)markup.Length);
        WriteBigEndian(result, recordZeroOffset + 8, (ushort)1);
        WriteBigEndian(result, recordZeroOffset + 10, (ushort)4096);
        WriteBigEndian(result, recordZeroOffset + 12, (ushort)0);
        Encoding.ASCII.GetBytes("MOBI").CopyTo(result, recordZeroOffset + 16);
        WriteBigEndian(result, recordZeroOffset + 20, (uint)232);
        WriteBigEndian(result, recordZeroOffset + 24, (uint)2);
        WriteBigEndian(result, recordZeroOffset + 44, (uint)65001);
        WriteBigEndian(result, recordZeroOffset + 52, (uint)6);
        WriteBigEndian(result, recordZeroOffset + 124, (uint)2);

        markup.CopyTo(result, textOffset);
        OnePixelPng.CopyTo(result, imageOffset);
        return result;
    }

    private static void WriteBigEndian(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteBigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed class TemporaryBook : IDisposable
    {
        private readonly string directory;

        public TemporaryBook(string extension)
        {
            directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"openshelf-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, $"fixture{extension}");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
