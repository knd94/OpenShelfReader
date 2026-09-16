using OpenShelf.Core;
using OpenShelf.Infrastructure;
using System.Text;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class ManagedBookImportServiceTests
{
    [Fact]
    public async Task FolderImportIsRecursiveManagedAndDoesNotMutateSources()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourceDirectory = System.IO.Path.Combine(
            temporaryDirectory.Path,
            "user-library");
        var nestedDirectory = System.IO.Path.Combine(sourceDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var sourcePath = System.IO.Path.Combine(nestedDirectory, "Story.txt");
        var originalBytes = "A book with an image reference and text."u8.ToArray();
        await File.WriteAllBytesAsync(
            sourcePath,
            originalBytes,
            TestContext.Current.CancellationToken);
        var originalTimestamp = File.GetLastWriteTimeUtc(sourcePath);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(nestedDirectory, "ignored.bin"),
            "not a supported extension",
            TestContext.Current.CancellationToken);

        var appDataRoot = System.IO.Path.Combine(temporaryDirectory.Path, "app-data");
        var paths = new PlatformAppDataPaths(appDataRoot);
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var adapter = new RecordingTextAdapter();
        var registry = new AdapterBookFormatRegistry([adapter]);
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            registry);
        var progress = new RecordingProgress<ImportBatchProgress>();

        var result = await importer.ImportFolderAsync(
            sourceDirectory,
            progress,
            TestContext.Current.CancellationToken);

        var imported = Assert.Single(result.Items);
        Assert.Equal(ImportOutcome.Imported, imported.Outcome);
        Assert.Equal(1, result.Imported);
        Assert.Equal(
            originalBytes,
            await File.ReadAllBytesAsync(
                sourcePath,
                TestContext.Current.CancellationToken));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(sourcePath));

        var recordedRequest = Assert.Single(adapter.Requests);
        Assert.NotEqual(
            System.IO.Path.GetFullPath(sourcePath),
            System.IO.Path.GetFullPath(recordedRequest.FilePath));
        Assert.StartsWith(
            System.IO.Path.GetFullPath(paths.LibraryDirectory),
            System.IO.Path.GetFullPath(recordedRequest.FilePath),
            GetPathComparison());

        var managedBook = await repository.GetBookAsync(
            imported.BookId!.Value,
            TestContext.Current.CancellationToken);
        Assert.NotNull(managedBook);
        Assert.True(File.Exists(managedBook.ManagedPath));
        Assert.Equal(
            originalBytes,
            await File.ReadAllBytesAsync(
                managedBook.ManagedPath,
                TestContext.Current.CancellationToken));
        Assert.NotEqual(sourcePath, managedBook.ManagedPath);
        Assert.Equal(1, progress.Values.Last().Total);
        Assert.Equal(1, progress.Values.Last().Processed);
    }

    [Fact]
    public async Task FolderImportReportsScanAndCurrentFileBeforeAdapterStarts()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourceDirectory = System.IO.Path.Combine(temporaryDirectory.Path, "selected");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = System.IO.Path.Combine(sourceDirectory, "before-start.txt");
        await File.WriteAllTextAsync(
            sourcePath,
            "content",
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var progress = new RecordingProgress<ImportBatchProgress>();
        ImportBatchProgress? observedAtAdapterStart = null;
        var adapter = new RecordingTextAdapter
        {
            ImportStarted = () => observedAtAdapterStart = progress.Values.LastOrDefault()
        };
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            new AdapterBookFormatRegistry([adapter]));

        var result = await importer.ImportFolderAsync(
            sourceDirectory,
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(ImportOutcome.Imported, Assert.Single(result.Items).Outcome);
        var scan = progress.Values.First();
        Assert.Equal(0, scan.Processed);
        Assert.Equal(0, scan.Total);
        Assert.Equal(System.IO.Path.GetFullPath(sourceDirectory), scan.CurrentFile);
        Assert.Equal(ImportBatchStage.Scanning, scan.Stage);

        Assert.NotNull(observedAtAdapterStart);
        Assert.Equal(0, observedAtAdapterStart.Processed);
        Assert.Equal(1, observedAtAdapterStart.Total);
        Assert.Equal("before-start.txt", observedAtAdapterStart.CurrentFile);
        Assert.Equal(ImportBatchStage.Importing, observedAtAdapterStart.Stage);
        Assert.Equal(1, progress.Values.Last().Processed);
        Assert.Equal(1, progress.Values.Last().Total);
        Assert.Equal(ImportBatchStage.Complete, progress.Values.Last().Stage);
    }

    [Fact]
    public async Task Sha256DedupeDoesNotCreateASecondManagedCopy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "same.txt");
        await File.WriteAllTextAsync(
            sourcePath,
            "identical bytes",
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var adapter = new RecordingTextAdapter();
        var registry = new AdapterBookFormatRegistry([adapter]);
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            registry);

        var first = await importer.ImportFilesAsync(
            [sourcePath],
            progress: null,
            TestContext.Current.CancellationToken);
        var second = await importer.ImportFilesAsync(
            [sourcePath],
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ImportOutcome.Imported, Assert.Single(first.Items).Outcome);
        Assert.Equal(ImportOutcome.Duplicate, Assert.Single(second.Items).Outcome);
        Assert.Equal(
            Assert.Single(first.Items).BookId,
            Assert.Single(second.Items).BookId);
        Assert.Single(
            Directory.EnumerateFiles(paths.LibraryDirectory, "*.txt"));
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task CancellationProducesCancelledOutcomeAndCleansStagingFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "cancel.txt");
        await File.WriteAllTextAsync(
            sourcePath,
            new string('x', 2_000_000),
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var adapter = new RecordingTextAdapter();
        var registry = new AdapterBookFormatRegistry([adapter]);
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => importer.ImportFilesAsync(
                [sourcePath],
                progress: null,
                cancellation.Token));
        var stagingDirectory = System.IO.Path.Combine(paths.LibraryDirectory, ".staging");
        Assert.False(Directory.Exists(stagingDirectory)
            && Directory.EnumerateFiles(stagingDirectory).Any());
    }

    [Theory]
    [InlineData(ImportOutcome.PasswordRequired)]
    [InlineData(ImportOutcome.DrmProtected)]
    [InlineData(ImportOutcome.Corrupt)]
    public async Task PreservesClassifiedFormatImportOutcome(ImportOutcome outcome)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "classified.pdf");
        await File.WriteAllBytesAsync(
            sourcePath,
            "%PDF-classified"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var adapter = new ControllablePdfAdapter { ForcedOutcome = outcome };
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            new AdapterBookFormatRegistry([adapter]));

        var batch = await importer.ImportFilesAsync(
            [sourcePath],
            progress: null,
            TestContext.Current.CancellationToken);

        var result = Assert.Single(batch.Items);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal($"Classified as {outcome}.", result.Message);
        if (outcome == ImportOutcome.PasswordRequired)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.PasswordRetryToken));
        }
        else
        {
            Assert.Null(result.PasswordRetryToken);
        }
    }

    [Fact]
    public async Task PasswordRetryKeepsStableTokenAndNeverPersistsPassword()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "protected.pdf");
        var sourceBytes = "%PDF-encrypted-test-content"u8.ToArray();
        await File.WriteAllBytesAsync(
            sourcePath,
            sourceBytes,
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        const string correctPassword = "correct horse battery staple";
        var adapter = new ControllablePdfAdapter
        {
            RequiredPassword = correctPassword
        };
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            new AdapterBookFormatRegistry([adapter]));

        var firstBatch = await importer.ImportFilesAsync(
            [sourcePath],
            progress: null,
            TestContext.Current.CancellationToken);
        var first = Assert.Single(firstBatch.Items);
        Assert.Equal(ImportOutcome.PasswordRequired, first.Outcome);
        var retryToken = Assert.IsType<string>(first.PasswordRetryToken);

        var wrongPassword = await importer.RetryWithPasswordAsync(
            retryToken,
            "incorrect",
            progress: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(ImportOutcome.PasswordRequired, wrongPassword.Outcome);
        Assert.Equal(retryToken, wrongPassword.PasswordRetryToken);

        var progress = new RecordingProgress<BookImportProgress>();
        var imported = await importer.RetryWithPasswordAsync(
            retryToken,
            correctPassword,
            progress,
            TestContext.Current.CancellationToken);
        Assert.Equal(ImportOutcome.Imported, imported.Outcome);
        Assert.Null(imported.PasswordRetryToken);
        Assert.NotNull(imported.BookId);
        Assert.Equal(3, adapter.Attempts);
        Assert.Equal(2, adapter.AttemptsWithPassword);
        Assert.NotEmpty(progress.Values);

        var book = await repository.GetBookAsync(
            imported.BookId.Value,
            TestContext.Current.CancellationToken);
        Assert.NotNull(book);
        Assert.Equal(
            sourceBytes,
            await File.ReadAllBytesAsync(
                book.ManagedPath,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            sourceBytes,
            await File.ReadAllBytesAsync(
                sourcePath,
                TestContext.Current.CancellationToken));
        AssertPasswordNotPresent(paths.DatabasePath, correctPassword);
        AssertPasswordNotPresent(paths.DatabasePath + "-wal", correctPassword);

        await Assert.ThrowsAsync<ArgumentException>(
            () => importer.RetryWithPasswordAsync(
                retryToken,
                correctPassword,
                progress: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonPasswordRetryOutcomeInvalidatesToken()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "protected.pdf");
        await File.WriteAllBytesAsync(
            sourcePath,
            "%PDF-encrypted"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var paths = new PlatformAppDataPaths(
            System.IO.Path.Combine(temporaryDirectory.Path, "app-data"));
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var adapter = new ControllablePdfAdapter { RequiredPassword = "secret" };
        await using var importer = new ManagedBookImportService(
            paths,
            repository,
            new AdapterBookFormatRegistry([adapter]));

        var first = Assert.Single((await importer.ImportFilesAsync(
            [sourcePath],
            progress: null,
            TestContext.Current.CancellationToken)).Items);
        var retryToken = Assert.IsType<string>(first.PasswordRetryToken);
        adapter.ForcedOutcome = ImportOutcome.DrmProtected;

        var drm = await importer.RetryWithPasswordAsync(
            retryToken,
            "secret",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ImportOutcome.DrmProtected, drm.Outcome);
        Assert.Null(drm.PasswordRetryToken);
        await Assert.ThrowsAsync<ArgumentException>(
            () => importer.RetryWithPasswordAsync(
                retryToken,
                "secret",
                progress: null,
                TestContext.Current.CancellationToken));
    }

    private static void AssertPasswordNotPresent(string path, string password)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var encodedPassword = Encoding.UTF8.GetBytes(password);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(encodedPassword));
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
