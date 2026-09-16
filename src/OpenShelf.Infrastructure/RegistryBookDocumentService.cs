using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed class RegistryBookDocumentService : IBookDocumentService
{
    private const int HeaderLength = 4096;

    private readonly IBookFormatRegistry _registry;
    private readonly ImportSecurityLimits _limits;

    public RegistryBookDocumentService(
        IBookFormatRegistry registry,
        ImportSecurityLimits? limits = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _limits = limits ?? ImportSecurityLimits.Default;
    }

    public async Task<BookDocument> OpenAsync(
        LibraryBook book,
        string? password,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        var header = await ReadHeaderAsync(book.ManagedPath, cancellationToken)
            .ConfigureAwait(false);

        if (!_registry.TryResolve(book.ManagedPath, header, out var adapter)
            || adapter is null)
        {
            throw new NotSupportedException(
                $"No document adapter can open '{Path.GetFileName(book.ManagedPath)}'.");
        }

        var request = new BookImportRequest(
            book.ManagedPath,
            book.DisplayTitle,
            adapter.Format,
            book.ContentHash,
            password,
            _limits);
        var imported = await adapter
            .ImportAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        return imported.Document;
    }

    internal static async Task<byte[]> ReadHeaderAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: HeaderLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[Math.Min(HeaderLength, checked((int)Math.Min(stream.Length, HeaderLength)))];
        var offset = 0;
        while (offset < header.Length)
        {
            var read = await stream
                .ReadAsync(header.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == header.Length ? header : header[..offset];
    }
}
