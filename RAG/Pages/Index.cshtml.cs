using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using RAG.Services;

namespace RAG.Pages
{
    public class IndexModel(
        IndexingService indexing,
        BrowserPresence presence,
        DatasetRegistry datasets,
        VectorStoreProvider stores,
        RagService rag,
        OllamaClient ollama,
        IOcrEngine ocrService,
        SystemMetrics metrics,
        IOptions<RagOptions> options,
        ILogger<IndexModel> logger) : PageModel
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public RagOptions Options { get; } = options.Value;

        public string LibraryPath => datasets.RootPath;

        public void OnGet()
        {
        }

        // ---------- Browser presence ----------

        /// <summary>
        /// Held open for as long as the page is. The app watches these connections to know whether
        /// anyone still has it open, and shuts down when the last one goes.
        ///
        /// A connection rather than a polling heartbeat, because browsers throttle timers in
        /// background tabs to about once a minute — a tab behind another window would look shut.
        /// </summary>
        public async Task<IActionResult> OnGetKeepAliveAsync(CancellationToken ct)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            presence.Opened();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // A comment frame: it keeps the connection demonstrably alive and gives the
                    // write that notices a client which vanished without closing cleanly.
                    await Response.WriteAsync(": keep-alive\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The tab closed or navigated away, which is exactly what this is here to detect.
            }
            finally
            {
                presence.Closed();
            }

            return new EmptyResult();
        }

        // ---------- Ollama health ----------

        public async Task<IActionResult> OnGetHealthAsync(CancellationToken ct)
        {
            var (reachable, models, error) = await ollama.ProbeAsync(ct);

            return new JsonResult(new
            {
                reachable,
                error,
                chatModel = Options.ChatModel,
                embeddingModel = Options.EmbeddingModel,
                chatModelReady = models.Contains(Options.ChatModel),
                embeddingModelReady = models.Contains(Options.EmbeddingModel),
                ocr = ocrService.Describe(),
                ocrAvailable = ocrService.IsAvailable,
                models
            }, Json);
        }

        // ---------- The library ----------

        /// <summary>
        /// Lists every dataset. Deliberately reads only metadata and file headers — opening each
        /// database to count rows would mean a connection and a fresh journal per dataset just to
        /// draw a list, and one unopenable database would take the whole list down with it.
        /// </summary>
        public IActionResult OnGetDatasets()
        {
            var copy = datasets.GetCopyStatus();
            var running = indexing.GetStatus();

            var list = datasets.List().Select(dataset =>
            {
                var inspection = DatabaseGuard.Inspect(dataset.DatabasePath);

                return new
                {
                    dataset.Id,
                    dataset.DisplayName,
                    dataset.Description,
                    dataset.DocumentCount,
                    dataset.ChunkCount,
                    dataset.CountsKnown,
                    dataset.EmbeddingModel,
                    dataset.EmbeddingDimension,
                    dataset.DatabasePath,
                    sizeMb = Math.Round(dataset.DatabaseBytes / 1024.0 / 1024.0, 1),
                    createdUtc = dataset.CreatedUtc,
                    lastIndexedUtc = dataset.LastIndexedUtc,
                    healthy = inspection.IsSafeToOpen,
                    health = inspection.Message,
                    indexing = string.Equals(running.DatasetId, dataset.Id, StringComparison.OrdinalIgnoreCase)
                              && running.State == IndexingState.Running
                };
            }).ToList();

            return new JsonResult(new
            {
                libraryPath = datasets.RootPath,
                datasets = list,
                copy = new
                {
                    state = copy.State.ToString(),
                    copy.Source,
                    copy.Target,
                    copy.PercentComplete,
                    copy.Error
                }
            }, Json);
        }

        /// <summary>
        /// Opens one dataset and reports what is actually in it. This is the call that makes a
        /// dataset the active one, so everything else is released.
        /// </summary>
        public IActionResult OnGetDataset(string? dataset)
        {
            if (string.IsNullOrWhiteSpace(dataset) || !datasets.Exists(dataset))
                return new JsonResult(new { error = "That dataset no longer exists." }, Json);

            try
            {
                var store = stores.Activate(dataset);
                var info = datasets.Load(dataset);
                var totals = store.GetStats(null);
                var dimension = store.GetEmbeddingDimension();

                // Keeps the library list honest for indexes that predate the stored counts.
                datasets.NoteOpened(dataset, dimension, totals);

                return new JsonResult(new
                {
                    info.Id,
                    info.DisplayName,
                    info.Description,
                    info.DatabasePath,
                    sizeMb = Math.Round(store.DatabaseBytes / 1024.0 / 1024.0, 1),
                    documentCount = totals.DocumentCount,
                    chunkCount = totals.ChunkCount,
                    lastIndexedUtc = totals.LastIndexedUtc,
                    embeddingDimension = dimension,
                    embeddingModel = info.EmbeddingModel,
                    modelMismatch = dimension > 0
                                    && !string.IsNullOrEmpty(info.EmbeddingModel)
                                    && info.EmbeddingModel != Options.EmbeddingModel,
                    searchMode = totals.ChunkCount == 0 ? null : store.DescribeSearchMode(null),
                    folders = store.GetFolders().Select(f => new
                    {
                        f.Folder,
                        f.DocumentCount,
                        f.ChunkCount,
                        lastIndexedUtc = f.LastIndexedUtc,
                        onDisk = Directory.Exists(f.Folder)
                    })
                }, Json);
            }
            catch (DatabaseUnsafeException ex)
            {
                return new JsonResult(new { error = ex.Message, canQuarantine = true }, Json);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not open dataset {Dataset}", dataset);
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        public IActionResult OnPostCreateDataset([FromBody] DatasetRequest? request)
        {
            try
            {
                var created = datasets.Create(request?.Name ?? "", request?.Description ?? "");
                return new JsonResult(new { created.Id, created.DisplayName }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        public IActionResult OnPostRenameDataset([FromBody] DatasetRequest? request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Dataset))
                    return new JsonResult(new { error = "No dataset selected." }, Json);

                var renamed = datasets.Rename(request.Dataset, request.Name ?? "", request.Description);
                return new JsonResult(new { renamed.Id, renamed.DisplayName }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        public IActionResult OnPostDeleteDataset([FromBody] DatasetRequest? request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Dataset))
                    return new JsonResult(new { error = "No dataset selected." }, Json);

                if (indexing.IsRunning && indexing.GetStatus().DatasetId == request.Dataset)
                    return new JsonResult(new { error = "That dataset is being indexed right now." }, Json);

                // The folder cannot be moved while SQLite still holds the file open.
                stores.Release(request.Dataset);

                var target = datasets.Delete(request.Dataset, DateTime.UtcNow);
                return new JsonResult(new { deleted = true, movedTo = target }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        public IActionResult OnPostDuplicateDataset([FromBody] DatasetRequest? request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Dataset))
                    return new JsonResult(new { error = "No dataset selected." }, Json);

                // Checkpointed and closed first, so the copy is a complete database rather than one
                // whose most recent writes are still sitting in a journal that is not being copied.
                stores.Release(request.Dataset);

                datasets.StartDuplicate(request.Dataset, request.Name ?? "");
                return new JsonResult(new { started = true }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        /// <summary>
        /// Moves a suspect write-ahead log out of the way so the database can be opened. Offered
        /// only when <see cref="DatabaseGuard"/> has concluded the journal belongs elsewhere.
        /// </summary>
        public IActionResult OnPostQuarantineJournal([FromBody] DatasetRequest? request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Dataset))
                    return new JsonResult(new { error = "No dataset selected." }, Json);

                var databasePath = datasets.DatabasePathFor(request.Dataset);
                var inspection = DatabaseGuard.Inspect(databasePath);

                if (inspection.Verdict != DatabaseVerdict.ForeignJournal)
                    return new JsonResult(new { error = "There is no suspect journal to quarantine." }, Json);

                stores.Release(request.Dataset);
                var movedTo = DatabaseGuard.Quarantine(databasePath, DateTime.UtcNow);

                logger.LogWarning("Quarantined journal for {Dataset} to {Target}", request.Dataset, movedTo);
                return new JsonResult(new { quarantined = true, movedTo }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        // ---------- Folder picking ----------

        /// <summary>
        /// A browser file picker only exposes file contents, never a real server-side path, so the
        /// folder is chosen by walking the file system from the server instead. Fine for an app
        /// that only ever runs on localhost.
        /// </summary>
        public IActionResult OnGetBrowse(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new { name = d.Name, path = d.RootDirectory.FullName })
                        .ToList();

                    return new JsonResult(new { current = (string?)null, parent = (string?)null, folders = drives }, Json);
                }

                var directory = new DirectoryInfo(Path.GetFullPath(path));
                if (!directory.Exists) return new JsonResult(new { error = "That folder does not exist." }, Json);

                var folders = directory
                    .EnumerateDirectories()
                    .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(d => d.Name)
                    .Select(d => new { name = d.Name, path = d.FullName })
                    .ToList();

                var fileCount = directory
                    .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = Options.SearchRecursively, IgnoreInaccessible = true })
                    .Count(f => RagOptions.SupportedExtensions.Contains(f.Extension.ToLowerInvariant()));

                return new JsonResult(new
                {
                    current = directory.FullName,
                    parent = directory.Parent?.FullName,
                    folders,
                    fileCount
                }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        // ---------- Indexing ----------

        public IActionResult OnPostStartIndex([FromBody] StartIndexRequest? request)
        {
            try
            {
                if (request is null) return new JsonResult(new { started = false, error = "Malformed request body." }, Json);

                indexing.Start(request.Dataset, request.Folder, request.Rebuild);
                return new JsonResult(new { started = true }, Json);
            }
            catch (DatabaseUnsafeException ex)
            {
                return new JsonResult(new { started = false, error = ex.Message, canQuarantine = true }, Json);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start indexing for {Folder}", request?.Folder);
                return new JsonResult(new { started = false, error = ex.Message }, Json);
            }
        }

        public IActionResult OnPostCancelIndex()
        {
            indexing.Cancel();
            return new JsonResult(new { cancelled = true }, Json);
        }

        public IActionResult OnGetIndexStatus(string? dataset, string? folder)
        {
            var status = indexing.GetStatus();

            FolderStats? stats = null;
            List<object>? documents = null;
            string? searchMode = null;
            string? error = null;

            if (!string.IsNullOrWhiteSpace(dataset) && datasets.Exists(dataset))
            {
                try
                {
                    var store = stores.For(dataset);

                    // An empty folder means "the whole dataset", which is what the store's null means.
                    var key = string.IsNullOrWhiteSpace(folder) ? null : VectorStore.NormalizeFolder(folder);

                    stats = store.GetStats(key);
                    searchMode = stats.ChunkCount == 0 ? null : store.DescribeSearchMode(key);
                    documents = store.GetDocuments(key)
                        .Select(d => (object)new { d.FileName, d.Path, d.ChunkCount, indexedUtc = d.IndexedUtc })
                        .ToList();
                }
                catch (DatabaseUnsafeException)
                {
                    // Reported by the dataset panel, which is the only place that can offer the
                    // fix. Repeating it here would just be the same warning without the button.
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            return new JsonResult(new
            {
                state = status.State.ToString(),
                status.DatasetId,
                status.Folder,
                status.TotalFiles,
                status.ProcessedFiles,
                status.SkippedUnchanged,
                status.ChunksAdded,
                status.RemovedStale,
                status.OcrPages,
                status.CurrentFile,
                error = status.Error ?? error,
                status.Warnings,
                status.PercentComplete,
                elapsedSeconds = Math.Round(status.ElapsedSeconds, 1),
                stats = stats is null ? null : new
                {
                    stats.DocumentCount,
                    stats.ChunkCount,
                    lastIndexedUtc = stats.LastIndexedUtc,
                    searchMode
                },
                documents
            }, Json);
        }

        /// <summary>Removes one source folder from a dataset, leaving the rest of it alone.</summary>
        public IActionResult OnPostClearIndex([FromBody] StartIndexRequest? request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Dataset))
                    return new JsonResult(new { error = "No dataset selected." }, Json);

                if (string.IsNullOrWhiteSpace(request.Folder))
                    return new JsonResult(new { error = "No folder selected." }, Json);

                var store = stores.For(request.Dataset);
                store.ClearFolder(VectorStore.NormalizeFolder(request.Folder));
                store.Checkpoint();

                datasets.NoteOpened(request.Dataset, store.GetEmbeddingDimension(), store.GetStats(null));
                return new JsonResult(new { cleared = true }, Json);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }, Json);
            }
        }

        // ---------- Resource metrics ----------

        public async Task<IActionResult> OnGetMetricsAsync(CancellationToken ct)
        {
            var m = await metrics.CollectAsync(ct);

            static double Mb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);
            static double Gb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 2);

            return new JsonResult(new
            {
                processRamMb = Mb(m.ProcessRamBytes),
                managedHeapMb = Mb(m.ManagedHeapBytes),
                vectorCacheMb = Mb(m.VectorCacheBytes),
                cpuPercent = Math.Round(m.ProcessCpuPercent, 1),
                systemRamTotalGb = Gb(m.SystemRamTotalBytes),
                systemRamAvailableGb = Gb(m.SystemRamAvailableBytes),
                systemRamUsedPercent = m.SystemRamTotalBytes == 0
                    ? 0
                    : Math.Round(100.0 * (m.SystemRamTotalBytes - m.SystemRamAvailableBytes) / m.SystemRamTotalBytes, 1),
                gpus = m.Gpus.Select(g => new
                {
                    g.Name,
                    totalGb = Gb(g.TotalBytes),
                    usedGb = Gb(g.UsedBytes),
                    usedPercent = g.TotalBytes == 0 ? 0 : Math.Round(100.0 * g.UsedBytes / g.TotalBytes, 1)
                }),
                models = m.Models.Select(x => new
                {
                    x.Name,
                    sizeGb = Gb(x.SizeBytes),
                    vramGb = Gb(x.VramBytes),
                    x.PercentOnGpu
                }),
                m.Note
            }, Json);
        }

        // ---------- Asking ----------

        /// <summary>
        /// Streams the answer as Server-Sent Events. A local model can take many seconds to produce
        /// its first token, so tokens are pushed as they arrive rather than made to wait for a
        /// complete response.
        /// </summary>
        /// <remarks>
        /// Returns <see cref="EmptyResult"/> rather than being a void handler: a void page handler
        /// still runs the default PageResult afterwards, which tries to set headers that have
        /// already been sent and tears down the connection mid-stream.
        /// </remarks>
        public async Task<IActionResult> OnGetAskAsync(string dataset, string? folder, string q, CancellationToken ct)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                if (string.IsNullOrWhiteSpace(q))
                {
                    await SendAsync("error", new { message = "Please type a question." }, ct);
                    return new EmptyResult();
                }

                if (string.IsNullOrWhiteSpace(dataset) || !datasets.Exists(dataset))
                {
                    await SendAsync("error", new { message = "Choose a dataset before asking a question." }, ct);
                    return new EmptyResult();
                }

                // Blank means "search the whole dataset" rather than one of its folders.
                var scope = string.IsNullOrWhiteSpace(folder) ? null : folder;

                if (scope is not null && !Directory.Exists(scope))
                {
                    await SendAsync("error", new { message = $"Folder not found: {scope}" }, ct);
                    return new EmptyResult();
                }

                await SendAsync("stage", new { message = "Searching the index…" }, ct);

                var citations = await rag.RetrieveAsync(dataset, scope, q, ct);

                await SendAsync("sources", citations.Select(c => new
                {
                    c.Number,
                    c.FileName,
                    c.Path,
                    c.PageNumber,
                    score = Math.Round(c.Score, 3),
                    excerpt = c.Excerpt
                }), ct);

                await SendAsync("stage", new
                {
                    message = citations.Count == 0
                        ? "No matching passages found."
                        : $"Asking {Options.ChatModel}…"
                }, ct);

                await foreach (var token in rag.AnswerStreamAsync(q, citations, ct))
                {
                    await SendAsync("token", new { v = token }, ct);
                }

                await SendAsync("done", new { }, ct);
            }
            catch (OperationCanceledException)
            {
                // Browser navigated away or the user hit stop; nothing to report.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to answer question");
                try { await SendAsync("error", new { message = ex.Message }, CancellationToken.None); }
                catch { /* connection already gone */ }
            }

            return new EmptyResult();
        }

        private async Task SendAsync(string eventName, object payload, CancellationToken ct)
        {
            await Response.WriteAsync($"event: {eventName}\n", ct);
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        public sealed class StartIndexRequest
        {
            public string Dataset { get; set; } = "";
            public string Folder { get; set; } = "";
            public bool Rebuild { get; set; }
        }

        public sealed class DatasetRequest
        {
            public string Dataset { get; set; } = "";
            public string Name { get; set; } = "";
            public string? Description { get; set; }
        }
    }
}
