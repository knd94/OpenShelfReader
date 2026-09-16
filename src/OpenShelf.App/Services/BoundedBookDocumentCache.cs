using System;
using System.Collections.Generic;
using OpenShelf.Core;

namespace OpenShelf.App.Services;

internal sealed class BoundedBookDocumentCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CacheEntry> _entries = new();
    private readonly LinkedList<Guid> _leastRecentlyUsed = new();

    public BoundedBookDocumentCache(int capacity = 8)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Set(Guid bookId, BookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (_entries.Remove(bookId, out var existing))
            {
                _leastRecentlyUsed.Remove(existing.Node);
            }

            var node = _leastRecentlyUsed.AddLast(bookId);
            _entries[bookId] = new CacheEntry(document, node);
            while (_entries.Count > _capacity)
            {
                var oldest = _leastRecentlyUsed.First;
                if (oldest is null)
                {
                    break;
                }

                _leastRecentlyUsed.RemoveFirst();
                _entries.Remove(oldest.Value);
            }
        }
    }

    public bool TryGet(Guid bookId, out BookDocument? document)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(bookId, out var entry))
            {
                document = null;
                return false;
            }

            _leastRecentlyUsed.Remove(entry.Node);
            _leastRecentlyUsed.AddLast(entry.Node);
            document = entry.Document;
            return true;
        }
    }

    private sealed record CacheEntry(
        BookDocument Document,
        LinkedListNode<Guid> Node);
}
