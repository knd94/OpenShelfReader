using System.Text;
using Avalonia.Headless.XUnit;
using OpenShelf.App.Services;
using Xunit;

namespace OpenShelf.HeadlessTests;

public sealed class SafeSvgRasterizerTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [AvaloniaFact]
    public void Pure_svg_is_rasterized_with_bounded_dimensions()
    {
        var source = Bytes(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
              <defs>
                <linearGradient id="cover" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#ff4b52" />
                  <stop offset="1" stop-color="#701719" />
                </linearGradient>
              </defs>
              <rect width="200" height="100" rx="12" fill="url(#cover)" />
            </svg>
            """);

        var rendered = SafeSvgRasterizer.TryRasterize(
            source,
            out var bitmap,
            out var failure,
            maximumWidth: 400,
            maximumHeight: 400);

        Assert.True(rendered);
        Assert.Equal(SvgRasterizationFailure.None, failure);
        using var ownedBitmap = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(bitmap);
        Assert.Equal(400, ownedBitmap.PixelSize.Width);
        Assert.Equal(200, ownedBitmap.PixelSize.Height);
    }

    [AvaloniaFact]
    public void Fragment_references_and_bounded_data_raster_images_are_allowed()
    {
        var source = Bytes(
            $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">
              <defs><clipPath id="crop"><circle cx="16" cy="16" r="15" /></clipPath></defs>
              <image width="32" height="32" clip-path="url(#crop)"
                     href="data:image/png;base64,{{OnePixelPng}}" />
            </svg>
            """);

        var rendered = SafeSvgRasterizer.TryRasterize(
            source,
            out var bitmap,
            out var failure,
            maximumWidth: 128,
            maximumHeight: 128);

        Assert.True(rendered);
        Assert.Equal(SvgRasterizationFailure.None, failure);
        bitmap?.Dispose();
    }

    [AvaloniaFact]
    public void Active_content_and_external_references_are_rejected()
    {
        var unsafeDocuments = new[]
        {
            "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)' width='10' height='10'><rect width='10' height='10'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><script>run()</script></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><foreignObject><form /></foreignObject></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><image href='https://example.test/cover.png'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><use href='other.svg#icon'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><a href='javascript:alert(1)'><rect width='10' height='10'/></a></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><style>@import url('https://example.test/x.css');</style></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect style=\"fill:url('cover.png')\" width='10' height='10'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><image href='data:image/svg+xml;base64,PHN2Zy8+'/></svg>"
        };

        foreach (var document in unsafeDocuments)
        {
            var rendered = SafeSvgRasterizer.TryRasterize(
                Bytes(document),
                out var bitmap,
                out var failure,
                maximumWidth: 64,
                maximumHeight: 64);

            Assert.False(rendered);
            Assert.Null(bitmap);
            Assert.Equal(SvgRasterizationFailure.UnsafeContent, failure);
        }
    }

    [AvaloniaFact]
    public void Dtds_and_oversized_dimensions_fail_closed()
    {
        const string withDtd =
            "<!DOCTYPE svg [<!ENTITY xxe SYSTEM 'file:///private'>]><svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><text>&xxe;</text></svg>";
        var dtdRendered = SafeSvgRasterizer.TryRasterize(
            Bytes(withDtd),
            out var dtdBitmap,
            out var dtdFailure,
            maximumWidth: 64,
            maximumHeight: 64);

        Assert.False(dtdRendered);
        Assert.Null(dtdBitmap);
        Assert.Equal(SvgRasterizationFailure.InvalidXml, dtdFailure);

        const string oversized =
            "<svg xmlns='http://www.w3.org/2000/svg' width='50000' height='50000' viewBox='0 0 50000 50000'><rect width='1' height='1'/></svg>";
        var oversizedRendered = SafeSvgRasterizer.TryRasterize(
            Bytes(oversized),
            out var oversizedBitmap,
            out var oversizedFailure,
            maximumWidth: 64,
            maximumHeight: 64);

        Assert.False(oversizedRendered);
        Assert.Null(oversizedBitmap);
        Assert.Equal(SvgRasterizationFailure.LimitExceeded, oversizedFailure);
    }

    [AvaloniaFact]
    public void Cancellation_and_placeholder_paths_are_deterministic()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rendered = SafeSvgRasterizer.TryRasterize(
            Bytes("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10'/></svg>"),
            out var bitmap,
            out var failure,
            cancellationToken: cancelled.Token);

        Assert.False(rendered);
        Assert.Null(bitmap);
        Assert.Equal(SvgRasterizationFailure.Cancelled, failure);

        using var placeholder = SafeSvgRasterizer.RasterizeOrPlaceholder(
            Bytes("<svg><script /></svg>"),
            out var placeholderFailure);
        Assert.Equal(SvgRasterizationFailure.UnsafeContent, placeholderFailure);
        Assert.Equal(96, placeholder.PixelSize.Width);
        Assert.Equal(128, placeholder.PixelSize.Height);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
