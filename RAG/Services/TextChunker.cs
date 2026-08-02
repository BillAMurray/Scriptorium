using System.Text;

namespace RAG.Services;

public sealed record TextChunk(int PageNumber, string Text);

/// <summary>
/// Splits extracted text into overlapping windows small enough to embed and cheap to retrieve.
///
/// The strategy is "split on the biggest boundary that still fits": paragraphs first, then
/// sentences, then a hard character cut as a last resort. That keeps chunks semantically whole,
/// which is what makes similarity search work well.
/// </summary>
public static class TextChunker
{
    public static List<TextChunk> Chunk(IReadOnlyList<ExtractedPage> pages, int maxChars, int overlapChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        overlapChars = Math.Clamp(overlapChars, 0, maxChars / 2);

        var chunks = new List<TextChunk>();

        foreach (var page in pages)
        {
            foreach (var piece in ChunkOne(page.Text, maxChars, overlapChars))
            {
                if (piece.Length >= 30) chunks.Add(new TextChunk(page.PageNumber, piece));
            }
        }

        return chunks;
    }

    private static IEnumerable<string> ChunkOne(string text, int maxChars, int overlapChars)
    {
        var segments = SplitToFittingSegments(text, maxChars);

        var current = new StringBuilder();

        foreach (var segment in segments)
        {
            if (current.Length > 0 && current.Length + segment.Length + 1 > maxChars)
            {
                var finished = current.ToString().Trim();
                if (finished.Length > 0) yield return finished;

                // Start the next chunk with the tail of the one we just emitted, so a fact that
                // spans the boundary is fully present in at least one chunk.
                var tail = TakeTail(finished, overlapChars);
                current.Clear();
                if (tail.Length > 0) current.Append(tail).Append(' ');
            }

            current.Append(segment).Append(' ');
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>Breaks text down until every piece is at most <paramref name="maxChars"/> long.</summary>
    private static IEnumerable<string> SplitToFittingSegments(string text, int maxChars)
    {
        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (paragraph.Length <= maxChars)
            {
                yield return paragraph;
                continue;
            }

            foreach (var sentence in SplitSentences(paragraph))
            {
                if (sentence.Length <= maxChars)
                {
                    yield return sentence;
                    continue;
                }

                // Pathological case: no sentence punctuation at all (tables, OCR runs).
                for (var i = 0; i < sentence.Length; i += maxChars)
                {
                    yield return sentence.Substring(i, Math.Min(maxChars, sentence.Length - i));
                }
            }
        }
    }

    private static IEnumerable<string> SplitSentences(string paragraph)
    {
        var start = 0;

        for (var i = 0; i < paragraph.Length; i++)
        {
            if (paragraph[i] is not ('.' or '!' or '?')) continue;

            // Only treat it as a sentence end if whitespace follows, so "3.5" and "Dr. Smith"
            // don't get chopped mid-token any more than necessary.
            if (i + 1 < paragraph.Length && !char.IsWhiteSpace(paragraph[i + 1])) continue;

            var sentence = paragraph[start..(i + 1)].Trim();
            if (sentence.Length > 0) yield return sentence;
            start = i + 1;
        }

        var remainder = paragraph[start..].Trim();
        if (remainder.Length > 0) yield return remainder;
    }

    private static string TakeTail(string text, int overlapChars)
    {
        if (overlapChars <= 0 || text.Length <= overlapChars) return string.Empty;

        var tail = text[^overlapChars..];

        // Trim to a word boundary so the overlap doesn't begin mid-word.
        var space = tail.IndexOf(' ');
        return space >= 0 ? tail[(space + 1)..].Trim() : tail.Trim();
    }
}
