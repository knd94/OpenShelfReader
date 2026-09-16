using OpenShelf.Core;
using OpenShelf.Infrastructure;
using System.Collections.Immutable;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class RegistryAndSpeechTests
{
    [Fact]
    public void RegistryUsesExtensionCaseInsensitively()
    {
        var adapter = new RecordingTextAdapter();
        var registry = new AdapterBookFormatRegistry([adapter]);

        var resolved = registry.TryResolve(
            "BOOK.TXT",
            "content"u8,
            out var result);

        Assert.True(resolved);
        Assert.Same(adapter, result);
    }

    [Fact]
    public async Task DocumentServiceOpensManagedBookThroughRegistry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var managedPath = System.IO.Path.Combine(temporaryDirectory.Path, "book.txt");
        await File.WriteAllTextAsync(
            managedPath,
            "Readable content",
            TestContext.Current.CancellationToken);
        var adapter = new RecordingTextAdapter();
        var service = new RegistryBookDocumentService(
            new AdapterBookFormatRegistry([adapter]));
        var book = new LibraryBook(
            Guid.NewGuid(),
            managedPath,
            BookFormat.Txt,
            "revision-hash",
            new BookMetadata("Book", "Author"),
            null,
            null,
            null,
            null,
            false,
            null,
            DateTimeOffset.UtcNow,
            null,
            ImmutableArray<Category>.Empty);

        var document = await service.OpenAsync(
            book,
            password: null,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.IsType<ReflowableDocument>(document);
        var request = Assert.Single(adapter.Requests);
        Assert.Equal(managedPath, request.FilePath);
        Assert.Equal(book.ContentHash, request.RevisionHash);
    }

    [Fact]
    public void LocatorFindsBundledExecutableAndSiblingData()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var executableName = OperatingSystem.IsWindows()
            ? "espeak-ng.exe"
            : "espeak-ng";
        var executablePath = System.IO.Path.Combine(
            temporaryDirectory.Path,
            executableName);
        File.WriteAllBytes(executablePath, [0]);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }

        Directory.CreateDirectory(
            System.IO.Path.Combine(temporaryDirectory.Path, "espeak-ng-data"));

        var runtime = EspeakExecutableLocator.Find(
            new EspeakDiscoveryOptions(
                BundleRoot: temporaryDirectory.Path,
                SearchPath: false,
                SearchDefaultBundle: false));

        Assert.NotNull(runtime);
        Assert.Equal(
            System.IO.Path.GetFullPath(executablePath),
            runtime.ExecutablePath);
        Assert.Equal(
            System.IO.Path.GetFullPath(temporaryDirectory.Path),
            runtime.DataParentDirectory);
        Assert.True(runtime.IsBundled);
    }

    [Fact]
    public async Task SpeechEngineFallsBackToUnavailableWithoutAnExecutable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var engine = new EspeakSpeechEngine(
            new EspeakDiscoveryOptions(
                BundleRoot: temporaryDirectory.Path,
                SearchPath: false,
                SearchDefaultBundle: false));

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SpeechState.Unavailable, engine.State);
        Assert.Empty(engine.Voices);
        await engine.SpeakAsync(
            ["Nothing should launch."],
            SpeechOptions.Default,
            startIndex: 0,
            TestContext.Current.CancellationToken);
        Assert.Equal(SpeechState.Unavailable, engine.State);
    }

    [Fact]
    public async Task BundledWindowsSpeechEngineDiscoversBuiltInEnglishVoice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleRoot = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "espeak-ng",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        await using var engine = new EspeakSpeechEngine(
            new EspeakDiscoveryOptions(
                BundleRoot: bundleRoot,
                SearchPath: false,
                SearchDefaultBundle: false));

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(
            engine.Voices.Any(
                voice => voice.DisplayName.Contains(
                    "English",
                    StringComparison.OrdinalIgnoreCase)),
            $"No built-in English voice was found. State: {engine.State}. "
            + $"Bundle: {bundleRoot}. Error: {engine.InitializationError ?? "none"}");
    }
}
