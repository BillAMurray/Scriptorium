namespace RAG.Services;

/// <summary>
/// Recognises text on PDF pages that have no text layer. Implemented by <see cref="WindowsOcrService"/>
/// (the built-in Windows engine) and <see cref="OllamaOcrService"/> (a local vision model via Ollama);
/// which one is active is picked by <see cref="RagOptions.OcrProvider"/>.
/// </summary>
public interface IOcrEngine
{
    /// <summary>True when OCR is switched on and this engine is actually usable right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Describes the OCR setup for the UI, or why it is unavailable.</summary>
    string Describe();

    /// <summary>OCRs the requested pages of a PDF and returns the text keyed by 1-based page number.</summary>
    Task<Dictionary<int, string>> RecognizePagesAsync(
        string pdfPath,
        IReadOnlyList<int> pageNumbers,
        CancellationToken ct = default);
}
