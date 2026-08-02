using Microsoft.Extensions.Options;

namespace RAG.Services;

public enum IndexingState { Idle, Running, Completed, Failed, Cancelled }

/// <summary>Snapshot of an indexing run, polled by the browser to drive the progress bar.</summary>
public sealed class IndexingStatus
{
    public IndexingState State { get; init; } = IndexingState.Idle;

    /// <summary>Which dataset the run is writing to — the page greys out the others while it runs.</summary>
    public string DatasetId { get; init; } = "";

    public string Folder { get; init; } = "";
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int SkippedUnchanged { get; init; }
    public int ChunksAdded { get; init; }
    public int RemovedStale { get; init; }

    /// <summary>Pages recovered by OCR that would otherwise have been unsearchable.</summary>
    public int OcrPages { get; init; }
    public string? CurrentFile { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; init; } = [];
    public double ElapsedSeconds { get; init; }

    public int PercentComplete => TotalFiles == 0 ? 0 : (int)(100.0 * ProcessedFiles / TotalFiles);
}

/// <summary>
/// Walks a folder, extracts and chunks each file, embeds the chunks and stores them.
///
/// Runs as a single background task because indexing a folder of PDFs against a local model
/// can take minutes, and an HTTP request should not be sitting open for that long.
/// </summary>
public sealed class IndexingService(
    OllamaClient ollama,
    VectorStoreProvider stores,
    DatasetRegistry registry,
    DocumentTextExtractor extractor,
    IOptions<RagOptions> options,
    ILogger<IndexingService> logger)
{
    private readonly RagOptions _options = options.Value;
    private readonly Lock _gate = new();

    private Task? _current;
    private CancellationTokenSource? _cancellation;

    // Mutable counters guarded by _gate; snapshotted into an immutable IndexingStatus on read.
    private IndexingState _state = IndexingState.Idle;
    private string _datasetId = "";
    private string _folder = "";
    private int _totalFiles, _processedFiles, _skipped, _chunksAdded, _removedStale, _ocrPages;
    private string? _currentFile, _error;
    private List<string> _warnings = [];
    private DateTime _startedUtc;
    private DateTime? _finishedUtc;

    public IndexingStatus GetStatus()
    {
        lock (_gate)
        {
            var end = _finishedUtc ?? DateTime.UtcNow;
            return new IndexingStatus
            {
                State = _state,
                DatasetId = _datasetId,
                Folder = _folder,
                TotalFiles = _totalFiles,
                ProcessedFiles = _processedFiles,
                SkippedUnchanged = _skipped,
                ChunksAdded = _chunksAdded,
                RemovedStale = _removedStale,
                OcrPages = _ocrPages,
                CurrentFile = _currentFile,
                Error = _error,
                Warnings = [.. _warnings],
                ElapsedSeconds = _state == IndexingState.Idle ? 0 : (end - _startedUtc).TotalSeconds
            };
        }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _state == IndexingState.Running; }
    }

    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Kicks off an indexing run. Throws if the folder is unusable or a run is already going.</summary>
    public void Start(string datasetId, string folderInput, bool rebuildFromScratch)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
            throw new ArgumentException("Please choose a dataset first.");

        if (string.IsNullOrWhiteSpace(folderInput))
            throw new ArgumentException("Please choose a folder first.");

        if (!Directory.Exists(folderInput))
            throw new DirectoryNotFoundException($"Folder not found: {folderInput}");

        // Opened before the run is marked as started, so an unopenable database is reported to the
        // caller as a plain error instead of surfacing later as a failed background run.
        stores.For(datasetId);

        lock (_gate)
        {
            if (_state == IndexingState.Running)
                throw new InvalidOperationException("Indexing is already running.");

            _state = IndexingState.Running;
            _datasetId = datasetId;
            _folder = VectorStore.NormalizeFolder(folderInput);
            _totalFiles = _processedFiles = _skipped = _chunksAdded = _removedStale = _ocrPages = 0;
            _currentFile = _error = null;
            _warnings = [];
            _startedUtc = DateTime.UtcNow;
            _finishedUtc = null;
        }

        // Keeps the dataset open even if the page switches to another one mid-run.
        stores.Pin(datasetId);

        _cancellation = new CancellationTokenSource();
        _current = Task.Run(() => RunAsync(datasetId, folderInput, rebuildFromScratch, _cancellation.Token));
    }

    private async Task RunAsync(string datasetId, string folderInput, bool rebuildFromScratch, CancellationToken ct)
    {
        var folderKey = VectorStore.NormalizeFolder(folderInput);

        try
        {
            var store = stores.For(datasetId);

            if (rebuildFromScratch) store.ClearFolder(folderKey);

            var searchOption = _options.SearchRecursively
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var files = EnumerateFiles(folderInput, searchOption);

            lock (_gate) _totalFiles = files.Count;

            var removed = store.RemoveMissingDocuments(folderKey, files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            lock (_gate) _removedStale = removed;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) _currentFile = file.Name;

                try
                {
                    await IndexFileAsync(store, folderKey, file, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to index {Path}", file.FullName);
                    AddWarning($"{file.Name}: {ex.Message}");
                }

                lock (_gate) _processedFiles++;
            }

            lock (_gate)
            {
                _state = IndexingState.Completed;
                _currentFile = null;
                _finishedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _state = IndexingState.Cancelled;
                _currentFile = null;
                _finishedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing run failed for {Folder}", folderInput);
            lock (_gate)
            {
                _state = IndexingState.Failed;
                _error = ex.Message;
                _currentFile = null;
                _finishedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            // However the run ended, leave the dataset in a state that is safe to copy or move:
            // metadata written, and the write-ahead log folded back in rather than left beside the
            // database for a later open to misread.
            try
            {
                var store = stores.For(datasetId);
                registry.NoteIndexed(
                    datasetId,
                    _options.EmbeddingModel,
                    store.GetEmbeddingDimension(),
                    store.GetStats(null));
                store.Checkpoint();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not finalise dataset {Dataset}", datasetId);
            }

            stores.Unpin(datasetId);
        }
    }

    private async Task IndexFileAsync(VectorStore store, string folderKey, FileInfo file, CancellationToken ct)
    {
        var lastWrite = file.LastWriteTimeUtc;

        // Unchanged files cost nothing on a re-index, which is the whole point of tracking timestamps.
        if (store.IsUpToDate(folderKey, file.FullName, lastWrite, file.Length))
        {
            lock (_gate) _skipped++;
            return;
        }

        if (file.Length > _options.MaxFileSizeMb * 1024L * 1024L)
        {
            AddWarning($"{file.Name}: skipped, larger than {_options.MaxFileSizeMb} MB.");
            return;
        }

        lock (_gate) _currentFile = file.Name;

        var extraction = await extractor.ExtractAsync(file.FullName, ct);

        if (extraction.OcrPageCount > 0)
        {
            lock (_gate) _ocrPages += extraction.OcrPageCount;
        }

        if (extraction.Pages.Count == 0)
        {
            AddWarning(extraction.LooksScanned
                ? $"{file.Name}: no text found — a scanned document that OCR could not read either."
                : $"{file.Name}: no text could be extracted.");
            return;
        }

        if (extraction.LooksScanned)
            AddWarning($"{file.Name}: only part of the document yielded text, even after OCR.");

        var chunks = TextChunker.Chunk(extraction.Pages, _options.MaxChunkChars, _options.ChunkOverlapChars);
        if (chunks.Count == 0)
        {
            AddWarning($"{file.Name}: text was too short to index.");
            return;
        }

        // Embed in batches: one HTTP round-trip per chunk would be dramatically slower.
        var embeddings = new List<float[]>(chunks.Count);
        for (var offset = 0; offset < chunks.Count; offset += _options.EmbeddingBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = chunks
                .Skip(offset)
                .Take(_options.EmbeddingBatchSize)
                .Select(c => _options.EmbedDocumentPrefix + c.Text)
                .ToList();

            embeddings.AddRange(await ollama.EmbedAsync(batch, ct));
        }

        store.SaveDocument(folderKey, file.FullName, lastWrite, file.Length, extraction.TotalPages, chunks, embeddings);

        lock (_gate) _chunksAdded += chunks.Count;
        logger.LogInformation("Indexed {File}: {Chunks} chunks from {Pages} pages.", file.Name, chunks.Count, extraction.TotalPages);
    }

    private static List<FileInfo> EnumerateFiles(string folder, SearchOption searchOption)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        };

        return new DirectoryInfo(folder)
            .EnumerateFiles("*", enumerationOptions)
            .Where(f => RagOptions.SupportedExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.FullName)
            .ToList();
    }

    private void AddWarning(string message)
    {
        lock (_gate)
        {
            if (_warnings.Count < 100) _warnings.Add(message);
        }
    }
}
