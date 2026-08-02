using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RAG.Services;

/// <summary>
/// Reads text off pages that have no text layer — scanned books, essentially.
///
/// Uses the OCR engine built into Windows rather than Tesseract, so there is nothing to install
/// and no language data to download. Pages are rendered to bitmaps with PDFium (via PDFtoImage)
/// because PdfPig can read a PDF's text and structure but cannot rasterise it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrService
{
    private readonly RagOptions _options;
    private readonly ILogger<OcrService> _logger;
    private readonly Lazy<Windows.Globalization.Language?> _language;

    public OcrService(IOptions<RagOptions> options, ILogger<OcrService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _language = new Lazy<Windows.Globalization.Language?>(ResolveLanguage);
    }

    /// <summary>True when OCR is switched on and this machine actually has a usable engine.</summary>
    public bool IsAvailable => _options.EnableOcr && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && _language.Value is not null;

    /// <summary>Describes the OCR setup for the UI, or why it is unavailable.</summary>
    public string Describe()
    {
        if (!_options.EnableOcr) return "disabled in configuration";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return "unavailable — needs Windows 10 build 19041 or later";
        if (_language.Value is null)
        {
            var available = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
            return $"no engine for '{_options.OcrLanguage}' — installed languages: {(available.Length == 0 ? "none" : available)}";
        }
        return $"{_language.Value.LanguageTag} at {_options.OcrDpi} DPI";
    }

    private Windows.Globalization.Language? ResolveLanguage()
    {
        try
        {
            var requested = new Windows.Globalization.Language(_options.OcrLanguage);
            if (OcrEngine.TryCreateFromLanguage(requested) is not null) return requested;

            // Fall back to whatever the machine does have rather than silently doing nothing.
            var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            if (first is not null)
            {
                _logger.LogWarning("OCR language {Requested} unavailable; falling back to {Fallback}.",
                    _options.OcrLanguage, first.LanguageTag);
                return first;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialise the Windows OCR engine.");
        }

        return null;
    }

    /// <summary>
    /// OCRs the requested pages of a PDF and returns the text keyed by 1-based page number.
    /// Pages are processed concurrently; measured throughput plateaus around four workers, since
    /// rasterising rather than recognition is the limiting step.
    /// </summary>
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
            _logger.LogWarning(ex, "Could not read {Path} for OCR.", pdfPath);
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
            var engine = OcrEngine.TryCreateFromLanguage(_language.Value);
            if (engine is null) return null;

            // PDFtoImage pages are 0-based; ours are 1-based throughout the app.
            using var bitmap = PDFtoImage.Conversion.ToImage(
                pdfBytes,
                page: pageNumber - 1,
                options: new PDFtoImage.RenderOptions(Dpi: _options.OcrDpi));

            ct.ThrowIfCancellationRequested();

            using var software = await ToSoftwareBitmapAsync(bitmap);
            var result = await engine.RecognizeAsync(software);

            return result.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OCR failed on page {Page}.", pageNumber);
            return null;
        }
    }

    /// <summary>
    /// Bridges Skia to WinRT. Going via an encoded PNG is a little wasteful, but it is the
    /// route that reliably produces a SoftwareBitmap in the pixel format the OCR engine wants.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(data.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
