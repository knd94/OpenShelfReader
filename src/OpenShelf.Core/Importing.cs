using System.Collections.Immutable;

namespace OpenShelf.Core;

public sealed record ImportSecurityLimits(
    long MaximumInputBytes,
    long MaximumExpandedBytes,
    int MaximumArchiveEntries,
    int MaximumXmlDepth,
    long MaximumResourceBytes,
    long MaximumDecodedPixels)
{
    public static ImportSecurityLimits Default { get; } = new(
        MaximumInputBytes: 1_073_741_824,
        MaximumExpandedBytes: 2_147_483_648,
        MaximumArchiveEntries: 20_000,
        MaximumXmlDepth: 128,
        MaximumResourceBytes: 134_217_728,
        MaximumDecodedPixels: 100_000_000);
}

public sealed record BookImportRequest(
    string FilePath,
    string DisplayName,
    BookFormat Format,
    string RevisionHash,
    string? Password,
    ImportSecurityLimits Limits);

public sealed record BookImportProgress(
    string Stage,
    string? CurrentItem,
    long Completed,
    long? Total,
    double? Fraction);

public sealed record ImportedBook(
    BookFormat Format,
    BookMetadata Metadata,
    BookDocument Document,
    byte[]? CoverImage,
    string? CoverMediaType,
    ImmutableArray<string> Warnings);

public enum ImportOutcome
{
    Imported,
    Duplicate,
    Unsupported,
    PasswordRequired,
    DrmProtected,
    Corrupt,
    Cancelled,
    Failed
}

public sealed record ImportItemResult(
    string DisplayName,
    ImportOutcome Outcome,
    Guid? BookId = null,
    string? Message = null,
    string? PasswordRetryToken = null);

public sealed record ImportBatchProgress(
    int Processed,
    int Total,
    string? CurrentFile,
    int Imported,
    int Duplicates,
    int Failed,
    ImportBatchStage Stage = ImportBatchStage.Importing)
{
    public double Fraction => Total == 0 ? 0 : (double)Processed / Total;
}

public enum ImportBatchStage
{
    Scanning,
    Importing,
    Complete,
    Cancelled
}

public sealed record ImportBatchResult(ImmutableArray<ImportItemResult> Items)
{
    public int Imported => Items.Count(item => item.Outcome == ImportOutcome.Imported);
    public int Duplicates => Items.Count(item => item.Outcome == ImportOutcome.Duplicate);
    public int Failed => Items.Length - Imported - Duplicates;
}

public interface IBookFormatAdapter
{
    BookFormat Format { get; }
    IReadOnlySet<string> Extensions { get; }
    bool MatchesSignature(ReadOnlySpan<byte> header);

    Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IBookFormatRegistry
{
    IReadOnlyList<IBookFormatAdapter> Adapters { get; }
    bool TryResolve(string fileName, ReadOnlySpan<byte> header, out IBookFormatAdapter? adapter);
}

public interface IBookImportService
{
    Task<ImportBatchResult> ImportFilesAsync(
        IEnumerable<string> explicitFilePaths,
        IProgress<ImportBatchProgress>? progress,
        CancellationToken cancellationToken);

    Task<ImportBatchResult> ImportFolderAsync(
        string explicitlySelectedFolder,
        IProgress<ImportBatchProgress>? progress,
        CancellationToken cancellationToken);

    Task<ImportItemResult> RetryWithPasswordAsync(
        string passwordRetryToken,
        string password,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IResourceStore
{
    bool TryResolve(string basePath, string reference, out string normalizedResourceId);
    bool TryGet(string resourceId, out BookResource? resource);
}
