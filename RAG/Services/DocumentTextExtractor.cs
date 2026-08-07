using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RAG.Services;

/// <summary>One page (or, for plain text files, one whole file) of extracted text.</summary>
public sealed record ExtractedPage(int PageNumber, string Text);

public sealed record ExtractionResult(
    IReadOnlyList<ExtractedPage> Pages,
    int TotalPages,
    bool LooksScanned,
    int OcrPageCount = 0);

/// <summary>
/// Pulls plain text out of a file. PDFs go through PdfPig; everything else is read as text.
/// Pages that come back empty are handed to OCR, which is what makes scanned books searchable.
/// </summary>
public sealed partial class DocumentTextExtractor(
    IOcrEngine ocr,
    IOptions<RagOptions> options,
    ILogger<DocumentTextExtractor> logger)
{
    private readonly RagOptions _options = options.Value;

    public async Task<ExtractionResult> ExtractAsync(string path, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".pdf" ? await ExtractPdfAsync(path, ct) : ExtractPlainText(path);
    }

    private async Task<ExtractionResult> ExtractPdfAsync(string path, CancellationToken ct)
    {
        var (pages, totalPages, emptyPages) = ExtractPdfTextLayer(path);

        // Pages with little or no text are the scanned ones. OCR only those: running it over
        // pages that already have a text layer would be far slower and usually worse.
        var ocrPageCount = 0;
        if (ocr.IsAvailable && emptyPages.Count > 0)
        {
            logger.LogInformation("OCR: {Count} page(s) without a text layer in {File}.",
                emptyPages.Count, Path.GetFileName(path));

            var recognised = await ocr.RecognizePagesAsync(path, emptyPages, ct);

            foreach (var (pageNumber, text) in recognised)
            {
                var cleaned = CleanUp(text);
                if (cleaned.Length < _options.OcrMinCharsPerPage) continue;

                pages.Add(new ExtractedPage(pageNumber, cleaned));
                ocrPageCount++;
            }

            pages.Sort((a, b) => a.PageNumber.CompareTo(b.PageNumber));
        }

        var looksScanned = totalPages > 0 && pages.Count < totalPages * 0.2;
        return new ExtractionResult(pages, totalPages, looksScanned, ocrPageCount);
    }

    private (List<ExtractedPage> Pages, int TotalPages, List<int> EmptyPages) ExtractPdfTextLayer(string path)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

        var pages = new List<ExtractedPage>();
        var emptyPages = new List<int>();
        var totalPages = 0;

        foreach (var page in document.GetPages())
        {
            totalPages++;

            string text;
            try
            {
                // The default GetText() concatenates letters with no regard for word breaks on
                // some PDFs. Going through the word extractor gives far more reliable spacing.
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                text = string.Join(' ', words.Select(w => w.Text));

                if (string.IsNullOrWhiteSpace(text)) text = page.Text;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract text from page {Page} of {Path}", page.Number, path);
                emptyPages.Add(page.Number);
                continue;
            }

            text = CleanUp(text);

            // Too little text means the page is an image of text: a candidate for OCR.
            if (text.Length < _options.OcrMinCharsPerPage)
            {
                emptyPages.Add(page.Number);
                continue;
            }

            pages.Add(new ExtractedPage(page.Number, text));
        }

        return (pages, totalPages, emptyPages);
    }

    private static ExtractionResult ExtractPlainText(string path)
    {
        var text = CleanUp(File.ReadAllText(path));

        return string.IsNullOrWhiteSpace(text)
            ? new ExtractionResult([], 1, false)
            : new ExtractionResult([new ExtractedPage(1, text)], 1, false);
    }

    /// <summary>
    /// Normalises the whitespace soup that PDF extraction produces, and replaces typographic
    /// characters that carry no extra meaning but hurt both search and the model.
    /// </summary>
    private static string CleanUp(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                // Ligatures are common in typeset PDFs; "ﬁnal" should be "final".
                case 'ﬀ': builder.Append("ff"); continue;
                case 'ﬁ': builder.Append("fi"); continue;
                case 'ﬂ': builder.Append("fl"); continue;
                case 'ﬃ': builder.Append("ffi"); continue;
                case 'ﬄ': builder.Append("ffl"); continue;
                case '­': continue;                      // soft hyphen: invisible, drop it
                case ' ': builder.Append(' '); continue;  // non-breaking space
                case '‘' or '’': builder.Append('\''); continue;
                case '“' or '”': builder.Append('"'); continue;
            }

            builder.Append(char.IsControl(c) && c is not ('\n' or '\r' or '\t') ? ' ' : c);
        }

        var result = builder.ToString();
        result = HyphenLineBreak().Replace(result, "");   // re-join words split across lines
        result = ExcessNewlines().Replace(result, "\n\n");
        result = HorizontalWhitespace().Replace(result, " ");

        return result.Trim();
    }

    [GeneratedRegex(@"-\s*\n\s*")]
    private static partial Regex HyphenLineBreak();

    [GeneratedRegex(@"(\s*\n\s*){2,}")]
    private static partial Regex ExcessNewlines();

    [GeneratedRegex(@"[^\S\n]{2,}")]
    private static partial Regex HorizontalWhitespace();
}
