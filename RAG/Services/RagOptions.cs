namespace RAG.Services;

/// <summary>
/// Everything tunable about the RAG pipeline. Bound from the "Rag" section of appsettings.json.
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Where Ollama is listening. Default install is http://localhost:11434.</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// The model that writes the answer. RAG hands it the relevant passages, so this is a
    /// reading-comprehension job rather than a recall one — a model small enough to fit entirely
    /// in VRAM answers far faster than a larger one that spills onto the CPU.
    /// </summary>
    public string ChatModel { get; set; } = "gemma4:12b";

    /// <summary>
    /// The model that turns text into vectors. Must be an "embedding" capable model.
    /// Measured on an RTX 5070: arctic-embed:33m is roughly 2x faster than nomic-embed-text at
    /// the same chunk size, and it was purpose-trained for retrieval.
    /// </summary>
    public string EmbeddingModel { get; set; } = "snowflake-arctic-embed:33m";

    /// <summary>
    /// Embedding models are trained with their own task prefixes, and using the wrong ones costs
    /// retrieval quality. arctic-embed expects a bare document and a prefixed query; nomic-embed-text
    /// wants "search_document: " and "search_query: ". Change these together with the model.
    /// </summary>
    public string EmbedDocumentPrefix { get; set; } = "";
    public string EmbedQueryPrefix { get; set; } = "Represent this sentence for searching relevant passages: ";

    /// <summary>
    /// Target size of each chunk of text, in characters (~4 chars per token).
    /// Embedding cost is dominated by a fixed per-chunk overhead rather than by length, so larger
    /// chunks index dramatically faster — at the price of coarser retrieval.
    /// </summary>
    public int MaxChunkChars { get; set; } = 2500;

    /// <summary>How much text each chunk repeats from the previous one, so ideas that straddle a boundary aren't lost.</summary>
    public int ChunkOverlapChars { get; set; } = 400;

    /// <summary>How many chunks to send to Ollama's embedding endpoint per request.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// How many of the best-matching chunks get pasted into the prompt.
    /// Keep TopK x MaxChunkChars comfortably inside NumCtx (~4 chars per token).
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Chunks scoring below this cosine similarity are treated as irrelevant and dropped.
    /// Score distributions differ per embedding model, so retune this after switching: start low
    /// so nothing good is discarded, then raise it once you have seen real scores in the UI.
    /// </summary>
    public float MinScore { get; set; } = 0.15f;

    /// <summary>Context window size passed to Ollama. Must comfortably fit TopK chunks plus the answer.</summary>
    public int NumCtx { get; set; } = 16384;

    /// <summary>Low temperature keeps the model close to the source text instead of inventing.</summary>
    public float Temperature { get; set; } = 0.2f;

    /// <summary>
    /// Only meaningful for a reasoning model. Leaving it false skips the thinking phase, which is
    /// much faster and rarely missed here — the passages are handed to the model directly, so the
    /// work is reading comprehension rather than deduction.
    /// </summary>
    public bool Think { get; set; }

    public bool SearchRecursively { get; set; } = true;

    /// <summary>Files larger than this are skipped, to avoid a single monster PDF stalling the index.</summary>
    public int MaxFileSizeMb { get; set; } = 200;

    /// <summary>Open the app in the default browser on startup, so running it is a single step.</summary>
    public bool LaunchBrowser { get; set; } = true;

    /// <summary>
    /// Shut the app down once the last browser tab closes, so it behaves like a desktop
    /// application instead of a server left running in a forgotten console. Turn this off when
    /// running it as a long-lived background service.
    /// </summary>
    public bool ShutdownWhenBrowserCloses { get; set; } = true;

    /// <summary>
    /// How long to wait after the last tab disappears before shutting down. A refresh drops the
    /// connection and immediately remakes it, so this needs to be comfortably longer than a
    /// reload takes.
    /// </summary>
    public int BrowserGraceSeconds { get; set; } = 5;

    /// <summary>
    /// Folder holding the library. Each dataset is a subfolder with its own index database and a
    /// dataset.json, so a dataset can be backed up, moved or deleted as a single folder.
    /// Relative paths resolve against the app's content root.
    /// </summary>
    public string DataSetsRoot { get; set; } = "DataSets";

    /// <summary>Name of the SQLite index file inside each dataset folder.</summary>
    public string DatabaseFile { get; set; } = "rag-index.db";

    /// <summary>
    /// Above this many chunks in a folder, search streams vectors from disk instead of caching
    /// them in memory. Each cached vector costs dimension x 4 bytes — at 768 dimensions the
    /// default works out to roughly 900 MB. Raise it if you have RAM to spare and want faster
    /// repeat questions; lower it if the app is using more memory than you would like.
    /// </summary>
    public int MaxCachedVectors { get; set; } = 2_500_000;

    /// <summary>
    /// Run OCR on PDF pages that have no text layer. Without this, scanned books index as
    /// essentially nothing and are invisible to search.
    /// </summary>
    public bool EnableOcr { get; set; } = true;

    /// <summary>
    /// A page yielding fewer characters than this is treated as an image of text and sent to OCR.
    /// Also the minimum OCR output worth keeping, which discards blank and near-blank scans.
    /// </summary>
    public int OcrMinCharsPerPage { get; set; } = 100;

    /// <summary>
    /// Resolution used when rasterising a page for OCR. 300 reads old print noticeably better
    /// than 200; beyond that, cost rises faster than accuracy.
    /// </summary>
    public int OcrDpi { get; set; } = 300;

    /// <summary>
    /// Pages OCR'd concurrently. Measured throughput plateaus around 4 — rasterising, not
    /// recognition, is the limiting step.
    /// </summary>
    public int OcrMaxParallelism { get; set; } = 4;

    /// <summary>BCP-47 tag of the OCR language pack. Falls back to any installed language.</summary>
    public string OcrLanguage { get; set; } = "en-US";

    /// <summary>File types we know how to pull text out of.</summary>
    public static readonly string[] SupportedExtensions = [".pdf", ".txt", ".md", ".markdown", ".csv", ".log"];
}
