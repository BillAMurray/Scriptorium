using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace RAG.Services;

/// <summary>
/// Hands out one <see cref="VectorStore"/> per dataset and decides which ones stay open.
///
/// Only the dataset in use is kept open. A single index can hold well over a gigabyte of cached
/// vectors, so holding every dataset the user has looked at would grow without bound; switching
/// away releases the previous one. Closing also checkpoints its write-ahead log, which means an
/// inactive dataset folder is left clean — no journal for a later open to trip over.
/// </summary>
public sealed class VectorStoreProvider(
    DatasetRegistry registry,
    IOptions<RagOptions> options,
    ILogger<VectorStoreProvider> logger)
{
    private readonly ConcurrentDictionary<string, VectorStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    // Datasets that must stay open regardless of what the page is showing, because a background
    // indexing run is writing to them.
    private readonly ConcurrentDictionary<string, byte> _pinned = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private string? _active;

    /// <summary>Opens a dataset's store, creating the database if this is its first use.</summary>
    /// <exception cref="DatabaseUnsafeException">The database cannot be opened without damaging it.</exception>
    public VectorStore For(string datasetId)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
            throw new ArgumentException("Choose a dataset first.");

        if (!registry.Exists(datasetId))
            throw new DirectoryNotFoundException($"No dataset called \"{datasetId}\".");

        if (_stores.TryGetValue(datasetId, out var existing)) return existing;

        lock (_gate)
        {
            if (_stores.TryGetValue(datasetId, out existing)) return existing;

            // Deliberately not cached on failure: quarantining the journal should make the very
            // next attempt succeed, not replay a remembered exception.
            var store = new VectorStore(
                registry.DatabasePathFor(datasetId),
                options.Value.MaxCachedVectors,
                logger);

            _stores[datasetId] = store;
            return store;
        }
    }

    /// <summary>
    /// Marks a dataset as the one in use and closes the others. Returns its store.
    /// </summary>
    public VectorStore Activate(string datasetId)
    {
        var store = For(datasetId);

        lock (_gate)
        {
            if (string.Equals(_active, datasetId, StringComparison.OrdinalIgnoreCase)) return store;
            _active = datasetId;
        }

        foreach (var id in _stores.Keys)
        {
            if (string.Equals(id, datasetId, StringComparison.OrdinalIgnoreCase)) continue;
            if (_pinned.ContainsKey(id)) continue;
            Release(id);
        }

        return store;
    }

    /// <summary>Keeps a dataset open across dataset switches, for the duration of an indexing run.</summary>
    public void Pin(string datasetId) => _pinned[datasetId] = 0;

    public void Unpin(string datasetId) => _pinned.TryRemove(datasetId, out _);

    /// <summary>
    /// Closes a dataset: checkpoints its journal, drops its vector cache and releases its pooled
    /// connections. Must be called before the folder is moved, copied or deleted.
    /// </summary>
    public void Release(string datasetId)
    {
        if (!_stores.TryRemove(datasetId, out var store)) return;

        try
        {
            store.Close();
            logger.LogInformation("Closed dataset {Id}", datasetId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Problem closing dataset {Id}", datasetId);
        }

        lock (_gate)
        {
            if (string.Equals(_active, datasetId, StringComparison.OrdinalIgnoreCase)) _active = null;
        }
    }

    public void ReleaseAll()
    {
        foreach (var id in _stores.Keys) Release(id);
    }

    /// <summary>Total vector-cache memory across everything currently open, for the metrics panel.</summary>
    public long CachedVectorBytes => _stores.Values.Sum(store => store.CachedVectorBytes);

    public string? ActiveDatasetId
    {
        get { lock (_gate) return _active; }
    }
}
