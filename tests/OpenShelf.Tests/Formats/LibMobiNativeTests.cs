using OpenShelf.Core;
using OpenShelf.Formats;
using Xunit;

namespace OpenShelf.Tests.Formats;

public sealed class LibMobiNativeTests
{
    [Fact]
    public async Task BundledLibMobiOpensHuffDicCompressedBook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var libraryPath = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native",
            "libmobi.dll");
        var fixturePath = new[]
        {
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "sample-unicode-huffdic.mobi"),
            Path.Combine(
                AppContext.BaseDirectory,
                "Formats",
                "Fixtures",
                "sample-unicode-huffdic.mobi")
        }.FirstOrDefault(File.Exists) ?? Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "sample-unicode-huffdic.mobi");
        Assert.True(File.Exists(libraryPath), $"Missing native runtime: {libraryPath}");
        Assert.True(File.Exists(fixturePath), $"Missing fixture: {fixturePath}");

        var previous = Environment.GetEnvironmentVariable("OPENSHELF_LIBMOBI_PATH");
        try
        {
            Environment.SetEnvironmentVariable("OPENSHELF_LIBMOBI_PATH", libraryPath);
            var availability = LibMobiDiscovery.Probe();
            Assert.True(availability.NativeLibraryAvailable, availability.Diagnostic);

            var request = new BookImportRequest(
                fixturePath,
                Path.GetFileName(fixturePath),
                BookFormat.Mobi,
                string.Empty,
                null,
                ImportSecurityLimits.Default with
                {
                    MaximumInputBytes = 8 * 1024 * 1024,
                    MaximumExpandedBytes = 32 * 1024 * 1024
                });
            var imported = await new MobiFormatAdapter().ImportAsync(
                request,
                null,
                TestContext.Current.CancellationToken);

            var document = Assert.IsType<ReflowableDocument>(imported.Document);
            Assert.NotEmpty(document.Sections);
            Assert.True(document.NormalizedLength > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELF_LIBMOBI_PATH", previous);
        }
    }
}
