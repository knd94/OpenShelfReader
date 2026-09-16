using System.Security.Cryptography;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed record ResourceCacheOptions(int MaximumEntries, long MaximumBytes)
{
    public static ResourceCacheOptions Default { get; } =
        new(MaximumEntries: 128, MaximumBytes: 64L * 1024 * 1024);
}

public sealed record ResourceCacheSnapshot(
    int CachedPayloads,
    long CachedBytes,
    int MaximumEntries,
    long MaximumBytes);

public sealed class BookResourceStore : IResourceStore
{
    private const int MaximumPathLength = 8192;
    private const int MaximumResourceIdLength = 512;

    private readonly object _cacheSync = new();
    private readonly Dictionary<string, ResourceDescriptor> _resourcesById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resourceIdsByPath =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourcePayload> _payloadsByKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _payloadKeysByHash =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _leastRecentlyUsed = new();
    private readonly ResourceCacheOptions _cacheOptions;
    private long _cachedBytes;

    public BookResourceStore(
        ReflowableDocument document,
        ResourceCacheOptions? cacheOptions = null)
        : this(
            document?.Resources.Values
                ?? throw new ArgumentNullException(nameof(document)),
            cacheOptions)
    {
    }

    public BookResourceStore(
        IEnumerable<BookResource> resources,
        ResourceCacheOptions? cacheOptions = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _cacheOptions = ValidateOptions(cacheOptions ?? ResourceCacheOptions.Default);

        foreach (var resource in resources)
        {
            AddResource(resource);
        }
    }

    public int ResourceCount => _resourcesById.Count;

    public int UniquePayloadCount => _payloadsByKey.Count;

    public ResourceCacheSnapshot CacheSnapshot
    {
        get
        {
            lock (_cacheSync)
            {
                return new ResourceCacheSnapshot(
                    _cache.Count,
                    _cachedBytes,
                    _cacheOptions.MaximumEntries,
                    _cacheOptions.MaximumBytes);
            }
        }
    }

    public bool TryResolve(
        string basePath,
        string reference,
        out string normalizedResourceId)
    {
        normalizedResourceId = string.Empty;
        if (!TryDecodeRelativePath(reference, allowEmpty: false, out var decodedReference))
        {
            return false;
        }

        if (_resourcesById.ContainsKey(decodedReference))
        {
            normalizedResourceId = decodedReference;
            return true;
        }

        if (!TryGetBaseDirectorySegments(basePath, out var segments))
        {
            return false;
        }

        foreach (var segment in decodedReference.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        var normalizedPath = string.Join('/', segments);
        if (!_resourceIdsByPath.TryGetValue(normalizedPath, out var resourceId))
        {
            return false;
        }

        normalizedResourceId = resourceId;
        return true;
    }

    public bool TryGet(string resourceId, out BookResource? resource)
    {
        resource = null;
        if (string.IsNullOrWhiteSpace(resourceId)
            || !_resourcesById.TryGetValue(resourceId, out var descriptor))
        {
            return false;
        }

        byte[] data;
        lock (_cacheSync)
        {
            data = GetPayloadData(descriptor.PayloadKey).ToArray();
        }

        resource = new BookResource(
            descriptor.Id,
            descriptor.MediaType,
            data,
            descriptor.OriginalPath);
        return true;
    }

    public bool TryGetContentHash(string resourceId, out string contentHash)
    {
        contentHash = string.Empty;
        if (!_resourcesById.TryGetValue(resourceId, out var descriptor))
        {
            return false;
        }

        contentHash = _payloadsByKey[descriptor.PayloadKey].ContentHash;
        return true;
    }

    public bool IsCached(string resourceId)
    {
        if (!_resourcesById.TryGetValue(resourceId, out var descriptor))
        {
            return false;
        }

        lock (_cacheSync)
        {
            return _cache.ContainsKey(descriptor.PayloadKey);
        }
    }

    public void ClearCache()
    {
        lock (_cacheSync)
        {
            _cache.Clear();
            _leastRecentlyUsed.Clear();
            _cachedBytes = 0;
        }
    }

    private void AddResource(BookResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResourceId(resource.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.MediaType);
        ArgumentNullException.ThrowIfNull(resource.Data);

        var payload = GetOrAddPayload(resource.Data);
        if (_resourcesById.TryGetValue(resource.Id, out var existing))
        {
            if (!string.Equals(
                    existing.PayloadKey,
                    payload.Key,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.MediaType,
                    resource.MediaType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Resource id '{resource.Id}' is assigned to different content.",
                    nameof(resource));
            }

            AddPathAlias(resource.OriginalPath, existing);
            return;
        }

        var descriptor = new ResourceDescriptor(
            resource.Id,
            resource.MediaType,
            resource.OriginalPath,
            payload.Key);
        _resourcesById.Add(descriptor.Id, descriptor);
        AddPathAlias(resource.OriginalPath, descriptor);
    }

    private ResourcePayload GetOrAddPayload(byte[] source)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        if (_payloadKeysByHash.TryGetValue(contentHash, out var candidateKeys))
        {
            foreach (var candidateKey in candidateKeys)
            {
                var candidate = _payloadsByKey[candidateKey];
                if (source.AsSpan().SequenceEqual(candidate.SourceData))
                {
                    return candidate;
                }
            }
        }
        else
        {
            candidateKeys = [];
            _payloadKeysByHash.Add(contentHash, candidateKeys);
        }

        var key = candidateKeys.Count == 0
            ? contentHash
            : $"{contentHash}:collision-{candidateKeys.Count}";
        var payload = new ResourcePayload(
            key,
            contentHash,
            source.ToArray());
        candidateKeys.Add(key);
        _payloadsByKey.Add(key, payload);
        return payload;
    }

    private void AddPathAlias(
        string? originalPath,
        ResourceDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return;
        }

        if (!TryNormalizeStoredPath(originalPath, out var normalizedPath))
        {
            throw new ArgumentException(
                $"Resource '{descriptor.Id}' has an unsafe original path.",
                nameof(originalPath));
        }

        if (!_resourceIdsByPath.TryGetValue(normalizedPath, out var existingId))
        {
            _resourceIdsByPath.Add(normalizedPath, descriptor.Id);
            return;
        }

        if (string.Equals(existingId, descriptor.Id, StringComparison.Ordinal))
        {
            return;
        }

        var existing = _resourcesById[existingId];
        if (!string.Equals(
                existing.PayloadKey,
                descriptor.PayloadKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Resource path '{normalizedPath}' is assigned to different content.",
                nameof(originalPath));
        }
    }

    private byte[] GetPayloadData(string payloadKey)
    {
        if (_cache.TryGetValue(payloadKey, out var cached))
        {
            _leastRecentlyUsed.Remove(cached.Node);
            _leastRecentlyUsed.AddFirst(cached.Node);
            return cached.Data;
        }

        var data = _payloadsByKey[payloadKey].SourceData;
        if (_cacheOptions.MaximumEntries == 0
            || _cacheOptions.MaximumBytes == 0
            || data.LongLength > _cacheOptions.MaximumBytes)
        {
            return data;
        }

        while (_cache.Count >= _cacheOptions.MaximumEntries
               || _cachedBytes > _cacheOptions.MaximumBytes - data.LongLength)
        {
            EvictLeastRecentlyUsed();
        }

        var node = _leastRecentlyUsed.AddFirst(payloadKey);
        _cache.Add(payloadKey, new CacheEntry(data, node));
        _cachedBytes += data.LongLength;
        return data;
    }

    private void EvictLeastRecentlyUsed()
    {
        var node = _leastRecentlyUsed.Last
            ?? throw new InvalidOperationException("The resource cache is inconsistent.");
        _leastRecentlyUsed.RemoveLast();
        var removed = _cache[node.Value];
        _cache.Remove(node.Value);
        _cachedBytes -= removed.Data.LongLength;
    }

    private static bool TryGetBaseDirectorySegments(
        string? basePath,
        out List<string> segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return true;
        }

        var isDirectory = basePath.TrimEnd().EndsWith('/');
        if (!TryDecodeRelativePath(basePath, allowEmpty: true, out var decodedBase))
        {
            return false;
        }

        foreach (var segment in decodedBase.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return false;
            }

            segments.Add(segment);
        }

        if (!isDirectory && segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return true;
    }

    private static bool TryNormalizeStoredPath(
        string path,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!TryDecodeRelativePath(path, allowEmpty: false, out var decoded))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in decoded.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    private static bool TryDecodeRelativePath(
        string? value,
        bool allowEmpty,
        out string decoded)
    {
        decoded = string.Empty;
        if (value is null || value.Length > MaximumPathLength)
        {
            return false;
        }

        var candidate = value.Trim();
        var delimiterIndex = candidate.IndexOfAny(['#', '?']);
        if (delimiterIndex >= 0)
        {
            candidate = candidate[..delimiterIndex];
        }

        if (candidate.Length == 0)
        {
            return allowEmpty;
        }

        if (!HasValidPercentEncoding(candidate))
        {
            return false;
        }

        try
        {
            decoded = Uri.UnescapeDataString(candidate);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (decoded.Length == 0)
        {
            return allowEmpty;
        }

        if (decoded.Length > MaximumPathLength
            || decoded.StartsWith("/", StringComparison.Ordinal)
            || decoded.StartsWith("//", StringComparison.Ordinal)
            || decoded.Contains('\\')
            || decoded.Contains(':')
            || decoded.Any(char.IsControl)
            || Uri.TryCreate(decoded, UriKind.Absolute, out _))
        {
            decoded = string.Empty;
            return false;
        }

        return true;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static void ValidateResourceId(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (resourceId.Length > MaximumResourceIdLength
            || resourceId.Any(char.IsControl)
            || resourceId.Contains('/')
            || resourceId.Contains('\\')
            || resourceId.Contains('?')
            || resourceId.Contains('#')
            || resourceId.Contains(':')
            || resourceId is "." or "..")
        {
            throw new ArgumentException(
                $"Resource id '{resourceId}' is not a safe stable id.",
                nameof(resourceId));
        }
    }

    private static ResourceCacheOptions ValidateOptions(ResourceCacheOptions options)
    {
        if (options.MaximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumEntries cannot be negative.");
        }

        if (options.MaximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumBytes cannot be negative.");
        }

        return options;
    }

    private sealed record ResourceDescriptor(
        string Id,
        string MediaType,
        string? OriginalPath,
        string PayloadKey);

    private sealed record ResourcePayload(
        string Key,
        string ContentHash,
        byte[] SourceData);

    private sealed record CacheEntry(
        byte[] Data,
        LinkedListNode<string> Node);
}
