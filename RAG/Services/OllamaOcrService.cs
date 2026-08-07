using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace RAG.Services;

/// <summary>
/// Reads text off pages that have no text layer, using a local vision model served by Ollama
/// (by default Baidu's Unlimited-OCR) instead of the Windows OCR engine. Rasterises each page
/// with PDFium (via PDFtoImage) — same as <see cref="WindowsOcrService"/> — but recognition
/// happens over the network via Ollama's /api/chat rather than in-process.
///
/// Pages are still sent one at a time: the model supports true single-pass whole-document
/// parsing, but that would mean restructuring how pages flow through <see cref="DocumentTextExtractor"/>
/// for a benefit (cross-page context) that mostly matters for tables/paragraphs spanning a page
/// break. Per-page calls keep the existing page-indexed pipeline and are still a clear accuracy
/// upgrade over the Windows engine.
/// </summary>
public sealed partial class OllamaOcrService(HttpClient http, IOptions<RagOptions> options, ILogger<OllamaOcrService> logger) : IOcrEngine
{
    // This model family (DeepSeek-OCR lineage) was trained against this exact instruction — other
    // phrasings ("transcribe this page", etc.) make it hallucinate meta-commentary about its own
    // rules instead of transcribing. Verified by hand against several prompt variants.
    private const string Prompt = "Free OCR.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RagOptions _options = options.Value;

    public bool IsAvailable => _options.EnableOcr;

    public string Describe() => _options.EnableOcr
        ? $"{_options.OcrModel} via Ollama at {_options.OllamaBaseUrl}"
        : "disabled in configuration";

    public async Task<Dictionary<int, string>> RecognizePagesAsync(
        string pdfPath,
        IReadOnlyList<int> pageNumbers,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, string>();
        if (!IsAvailable || pageNumbers.Count == 0) return results;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(pdfPath, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {Path} for OCR.", pdfPath);
            return results;
        }

        var gate = new Lock();

        await Parallel.ForEachAsync(
            pageNumbers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.OcrMaxParallelism),
                CancellationToken = ct
            },
            async (pageNumber, token) =>
            {
                var text = await RecognizePageAsync(bytes, pageNumber, token);
                if (string.IsNullOrWhiteSpace(text)) return;

                lock (gate) results[pageNumber] = text;
            });

        return results;
    }

    private async Task<string?> RecognizePageAsync(byte[] pdfBytes, int pageNumber, CancellationToken ct)
    {
        try
        {
            // PDFtoImage pages are 0-based; ours are 1-based throughout the app.
            using var bitmap = PDFtoImage.Conversion.ToImage(
                pdfBytes,
                page: pageNumber - 1,
                options: new PDFtoImage.RenderOptions(Dpi: _options.OcrDpi));

            ct.ThrowIfCancellationRequested();

            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var base64 = Convert.ToBase64String(png.AsSpan());

            var request = new
            {
                model = _options.OcrModel,
                stream = false,
                messages = new[]
                {
                    new { role = "user", content = Prompt, images = new[] { base64 } }
                }
            };

            using var response = await http.PostAsJsonAsync("/api/chat", request, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Ollama OCR failed on page {Page}: {Status} {Body}", pageNumber, response.StatusCode, body);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, ct);
            return StripLayoutTags(payload?.Message?.Content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OCR failed on page {Page}.", pageNumber);
            return null;
        }
    }

    /// <summary>
    /// The model's native output tags every line with its detected element type and bounding box,
    /// e.g. "text [64, 190, 675, 245]Some paragraph text" — useful for layout-aware consumers, but
    /// noise for a text index. Strips the tag and keeps the transcribed text.
    /// </summary>
    private static string? StripLayoutTags(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return LayoutTag().Replace(content, "");
    }

    [GeneratedRegex(@"^[ \t]*[A-Za-z_]+[ \t]*\[\d+,\s*\d+,\s*\d+,\s*\d+\]", RegexOptions.Multiline)]
    private static partial Regex LayoutTag();

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? Content { get; set; }
    }
}
