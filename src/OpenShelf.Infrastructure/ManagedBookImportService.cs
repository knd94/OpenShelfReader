using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenShelf.Core;
using OpenShelf.Formats;

namespace OpenShelf.Infrastructure;

public sealed class ManagedBookImportService : IBookImportService, IAsyncDisposable
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly IAppDataPaths _paths;
    private readonly ILibraryRepository _repository;
    private readonly IBookFormatRegistry _registry;
    private readonly ImportSecurityLimits _limits;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly Dictionary<string, PasswordRetryState> _passwordRetries =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public ManagedBookImportService(
        IAppDataPaths paths,
        ILibraryRepository repository,
        IBookFormatRegistry registry,
        ImportSecurityLimits? limits = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _limits = limits ?? ImportSecurityLimits.Default;
    }

    public Task<ImportBatchResult> ImportFilesAsync(
        IEnumerable<string> explicitFilePaths,
        IProgress<ImportBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(explicitFilePaths);
        var files = explicitFilePaths
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .ToArray();
        return ImportCoreAsync(files, progress, cancellationToken);
    }

    public async Task<ImportBatchResult> ImportFolderAsync(
        string explicitlySelectedFolder,
        IProgress<ImportBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitlySelectedFolder);
        var folder = Path.GetFullPath(explicitlySelectedFolder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The import folder '{folder}' does not exist.");
        }

        var extensions = _registry.Adapters
            .SelectMany(adapter => adapter.Extensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new ImportBatchProgress(
            Processed: 0,
            Total: 0,
            CurrentFile: folder,
            Imported: 0,
            Duplicates: 0,
            Failed: 0,
            Stage: ImportBatchStage.Scanning));
        var files = await Task.Run(
                () => EnumerateSupportedFiles(folder, extensions, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return await ImportCoreAsync(files, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportItemResult> RetryWithPasswordAsync(
        string passwordRetryToken,
        string password,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordRetryToken);
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("A password is required.", nameof(password));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_passwordRetries.TryGetValue(passwordRetryToken, out var retry))
            {
                throw new ArgumentException(
                    "The password retry token is invalid or has expired.",
                    nameof(passwordRetryToken));
            }

            _paths.EnsureCreated();
            var stagingDirectory = Path.Combine(_paths.LibraryDirectory, ".staging");
            Directory.CreateDirectory(stagingDirectory);
            var result = await ImportOneAsync(
                    retry.SourcePath,
                    stagingDirectory,
                    password,
                    progress,
                    passwordRetryToken,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome is not ImportOutcome.PasswordRequired
                and not ImportOutcome.Cancelled)
            {
                _passwordRetries.Remove(passwordRetryToken);
            }

            return result;
        }
        finally
        {
            _importGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _importGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _passwordRetries.Clear();
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<ImportBatchResult> ImportCoreAsync(
        IReadOnlyList<string> files,
        IProgress<ImportBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var stagingDirectory = Path.Combine(_paths.LibraryDirectory, ".staging");
            Directory.CreateDirectory(stagingDirectory);

            var results = ImmutableArray.CreateBuilder<ImportItemResult>();
            ReportProgress(
                progress,
                results,
                files.Count,
                currentFile: null,
                stage: files.Count == 0
                    ? ImportBatchStage.Complete
                    : ImportBatchStage.Importing);

            foreach (var file in files)
            {
                var currentFile = Path.GetFileName(file);
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new ImportItemResult(
                        currentFile,
                        ImportOutcome.Cancelled,
                        Message: "Import cancelled."));
                    ReportProgress(
                        progress,
                        results,
                        files.Count,
                        currentFile,
                        ImportBatchStage.Cancelled);
                    break;
                }

                ReportProgress(
                    progress,
                    results,
                    files.Count,
                    currentFile,
                    ImportBatchStage.Importing);
                var item = await ImportOneAsync(
                        file,
                        stagingDirectory,
                        password: null,
                        formatProgress: null,
                        passwordRetryToken: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                results.Add(item);
                ReportProgress(
                    progress,
                    results,
                    files.Count,
                    currentFile,
                    item.Outcome == ImportOutcome.Cancelled
                        ? ImportBatchStage.Cancelled
                        : results.Count == files.Count
                            ? ImportBatchStage.Complete
                            : ImportBatchStage.Importing);

                if (item.Outcome == ImportOutcome.Cancelled)
                {
                    break;
                }
            }

            return new ImportBatchResult(results.ToImmutable());
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<ImportItemResult> ImportOneAsync(
        string sourcePath,
        string stagingDirectory,
        string? password,
        IProgress<BookImportProgress>? formatProgress,
        string? passwordRetryToken,
        CancellationToken cancellationToken)
    {
        var displayName = Path.GetFileName(sourcePath);
        string? stagingPath = null;
        string? committedPath = null;

        try
        {
            if (!File.Exists(sourcePath))
            {
                return new ImportItemResult(
                    displayName,
                    ImportOutcome.Failed,
                    Message: "The source file does not exist.");
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (sourceInfo.Length > _limits.MaximumInputBytes)
            {
                return new ImportItemResult(
                    displayName,
                    ImportOutcome.Failed,
                    Message: $"The file exceeds the {_limits.MaximumInputBytes} byte import limit.");
            }

            var header = await RegistryBookDocumentService
                .ReadHeaderAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!_registry.TryResolve(sourcePath, header, out var adapter) || adapter is null)
            {
                return new ImportItemResult(
                    displayName,
                    ImportOutcome.Unsupported,
                    Message: "No supported book adapter recognized this file.");
            }

            var sourceExtension = Path.GetExtension(sourcePath);
            stagingPath = Path.Combine(
                stagingDirectory,
                $"{Guid.NewGuid():N}.importing{sourceExtension}");
            var contentHash = await CopyAndHashAsync(
                    sourcePath,
                    stagingPath,
                    _limits.MaximumInputBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            var duplicate = await _repository
                .FindByHashAsync(contentHash, cancellationToken)
                .ConfigureAwait(false);
            if (duplicate is not null)
            {
                return new ImportItemResult(
                    displayName,
                    ImportOutcome.Duplicate,
                    duplicate.Id,
                    $"Already in the library as '{duplicate.DisplayTitle}'.");
            }

            var imported = await adapter
                .ImportAsync(
                    new BookImportRequest(
                        stagingPath,
                        displayName,
                        adapter.Format,
                        contentHash,
                        password,
                        _limits),
                    formatProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var bookId = Guid.NewGuid();
            var finalExtension = string.IsNullOrWhiteSpace(sourceExtension)
                ? GetCanonicalExtension(imported.Format)
                : sourceExtension.ToLowerInvariant();
            committedPath = Path.Combine(
                _paths.LibraryDirectory,
                $"{bookId:N}{finalExtension}");
            File.Move(stagingPath, committedPath);
            stagingPath = null;

            var book = new LibraryBook(
                bookId,
                committedPath,
                imported.Format,
                contentHash,
                imported.Metadata,
                TitleOverride: null,
                AuthorOverride: null,
                imported.CoverImage,
                imported.CoverMediaType,
                IsFavorite: false,
                ReadingPosition: null,
                DateTimeOffset.UtcNow,
                LastOpenedAt: null,
                ImmutableArray<Category>.Empty,
                EstimateWordCount(imported.Document.NormalizedLength));

            try
            {
                await _repository
                    .AddBookAsync(book, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                var racedDuplicate = await _repository
                    .FindByHashAsync(contentHash, CancellationToken.None)
                    .ConfigureAwait(false);
                if (racedDuplicate is not null)
                {
                    TryDeleteManagedFile(committedPath);
                    committedPath = null;
                    return new ImportItemResult(
                        displayName,
                        ImportOutcome.Duplicate,
                        racedDuplicate.Id,
                        $"Already in the library as '{racedDuplicate.DisplayTitle}'.");
                }

                throw;
            }
            catch
            {
                TryDeleteManagedFile(committedPath);
                committedPath = null;
                throw;
            }

            committedPath = null;
            return new ImportItemResult(
                displayName,
                ImportOutcome.Imported,
                bookId);
        }
        catch (OperationCanceledException)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Cancelled,
                Message: "Import cancelled.");
        }
        catch (FormatImportException exception)
        {
            var retryToken = exception.Outcome == ImportOutcome.PasswordRequired
                ? passwordRetryToken ?? RegisterPasswordRetry(sourcePath, displayName)
                : null;
            return new ImportItemResult(
                displayName,
                exception.Outcome,
                Message: exception.Message,
                PasswordRetryToken: retryToken);
        }
        catch (InvalidDataException exception)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Corrupt,
                Message: exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Unsupported,
                Message: exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Failed,
                Message: exception.Message);
        }
        catch (IOException exception)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Failed,
                Message: exception.Message);
        }
        catch (Exception exception)
        {
            return new ImportItemResult(
                displayName,
                ImportOutcome.Failed,
                Message: exception.Message);
        }
        finally
        {
            if (stagingPath is not null)
            {
                TryDeleteManagedFile(stagingPath);
            }

            if (committedPath is not null)
            {
                TryDeleteManagedFile(committedPath);
            }
        }
    }

    private static long EstimateWordCount(long normalizedLength) =>
        normalizedLength <= 0 ? 0 : Math.Max(1, normalizedLength / 6);

    private async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;

        try
        {
            while (true)
            {
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"The source grew beyond the {maximumBytes} byte import limit.");
                }

                hash.AppendData(buffer, 0, read);
                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private string RegisterPasswordRetry(string sourcePath, string displayName)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _passwordRetries.Add(
            token,
            new PasswordRetryState(sourcePath, displayName));
        return token;
    }

    private void TryDeleteManagedFile(string path)
    {
        try
        {
            var libraryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_paths.LibraryDirectory));
            var candidate = Path.GetFullPath(path);
            var prefix = libraryRoot + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(prefix, GetPathComparison()) && File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch
        {
            // Cleanup is best-effort; never widen it to the user-selected source location.
        }
    }

    private static IReadOnlyList<string> EnumerateSupportedFiles(
        string root,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<string> childFiles;
            IEnumerable<string> childDirectories;
            try
            {
                childFiles = Directory.EnumerateFiles(directory);
                childDirectories = Directory.EnumerateDirectories(directory);

                foreach (var file in childFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (extensions.Contains(Path.GetExtension(file)))
                    {
                        files.Add(Path.GetFullPath(file));
                    }
                }

                foreach (var childDirectory in childDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(childDirectory);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        directories.Push(childDirectory);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible child does not invalidate the explicitly selected tree.
            }
            catch (IOException)
            {
                // A disappearing child does not invalidate the rest of the import tree.
            }
        }

        files.Sort(GetPathComparer());
        return files;
    }

    private static void ReportProgress(
        IProgress<ImportBatchProgress>? progress,
        ImmutableArray<ImportItemResult>.Builder results,
        int total,
        string? currentFile,
        ImportBatchStage stage)
    {
        if (progress is null)
        {
            return;
        }

        var imported = 0;
        var duplicates = 0;
        foreach (var result in results)
        {
            if (result.Outcome == ImportOutcome.Imported)
            {
                imported++;
            }
            else if (result.Outcome == ImportOutcome.Duplicate)
            {
                duplicates++;
            }
        }

        progress.Report(new ImportBatchProgress(
            results.Count,
            total,
            currentFile,
            imported,
            duplicates,
            results.Count - imported - duplicates,
            stage));
    }

    private static string GetCanonicalExtension(BookFormat format) =>
        SupportedBookFormats.Extensions
            .FirstOrDefault(pair => pair.Value == format)
            .Key
        ?? $".{format.ToString().ToLowerInvariant()}";

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record PasswordRetryState(string SourcePath, string DisplayName);
}
