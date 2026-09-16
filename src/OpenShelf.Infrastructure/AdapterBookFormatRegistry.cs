using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed class AdapterBookFormatRegistry : IBookFormatRegistry
{
    private readonly IReadOnlyList<IBookFormatAdapter> _adapters;

    public AdapterBookFormatRegistry(IEnumerable<IBookFormatAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToArray();

        if (_adapters.Any(adapter => adapter is null))
        {
            throw new ArgumentException("Format adapters cannot contain null entries.", nameof(adapters));
        }

        var duplicateFormat = _adapters
            .GroupBy(adapter => adapter.Format)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFormat is not null)
        {
            throw new ArgumentException(
                $"More than one adapter is registered for {duplicateFormat.Key}.",
                nameof(adapters));
        }
    }

    public IReadOnlyList<IBookFormatAdapter> Adapters => _adapters;

    public bool TryResolve(
        string fileName,
        ReadOnlySpan<byte> header,
        out IBookFormatAdapter? adapter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var extension = Path.GetExtension(fileName);
        IBookFormatAdapter? extensionFallback = null;

        foreach (var candidate in _adapters)
        {
            if (!candidate.Extensions.Any(
                    value => string.Equals(
                        value,
                        extension,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            extensionFallback ??= candidate;
            if (SafelyMatchesSignature(candidate, header))
            {
                adapter = candidate;
                return true;
            }
        }

        if (extensionFallback is not null)
        {
            adapter = extensionFallback;
            return true;
        }

        foreach (var candidate in _adapters)
        {
            if (SafelyMatchesSignature(candidate, header))
            {
                adapter = candidate;
                return true;
            }
        }

        adapter = null;
        return false;
    }

    private static bool SafelyMatchesSignature(
        IBookFormatAdapter adapter,
        ReadOnlySpan<byte> header)
    {
        try
        {
            return adapter.MatchesSignature(header);
        }
        catch
        {
            return false;
        }
    }
}
