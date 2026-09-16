using OpenShelf.Core;

namespace OpenShelf.Formats;

public sealed class BookFormatRegistry : IBookFormatRegistry
{
    private readonly IReadOnlyList<IBookFormatAdapter> adapters;

    public BookFormatRegistry()
        : this(
        [
            new EpubFormatAdapter(),
            new PdfFormatAdapter(),
            new MobiFormatAdapter(),
            new Azw3FormatAdapter(),
            new Fb2FormatAdapter(),
            new TxtFormatAdapter(),
            new RtfFormatAdapter(),
            new DocxFormatAdapter()
        ])
    {
    }

    public BookFormatRegistry(IEnumerable<IBookFormatAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var materialized = adapters.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one format adapter is required.", nameof(adapters));
        }

        var duplicate = materialized
            .GroupBy(adapter => adapter.Format)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"More than one adapter was registered for {duplicate.Key}.",
                nameof(adapters));
        }

        this.adapters = Array.AsReadOnly(materialized);
    }

    public IReadOnlyList<IBookFormatAdapter> Adapters => adapters;

    public bool TryResolve(
        string fileName,
        ReadOnlySpan<byte> header,
        out IBookFormatAdapter? adapter)
    {
        adapter = null;
        var extension = Path.GetExtension(fileName);
        var headerBytes = header.ToArray();

        if (!string.IsNullOrWhiteSpace(extension))
        {
            adapter = adapters.FirstOrDefault(candidate =>
                candidate.Extensions.Contains(extension) &&
                (headerBytes.Length == 0 || candidate.MatchesSignature(headerBytes)));

            // ZIP based formats cannot be distinguished from a short local header.
            // The explicit extension remains authoritative for EPUB and DOCX.
            adapter ??= adapters.FirstOrDefault(candidate =>
                candidate.Extensions.Contains(extension) &&
                (candidate.Format is BookFormat.Epub or BookFormat.Docx));

            if (adapter is not null)
            {
                return true;
            }
        }

        var signatureMatches = adapters
            .Where(candidate => candidate.MatchesSignature(headerBytes))
            .Take(2)
            .ToArray();
        if (signatureMatches.Length == 1)
        {
            adapter = signatureMatches[0];
            return true;
        }

        return false;
    }
}
