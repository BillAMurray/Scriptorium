using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace RAG.Services;

public sealed record Citation(int Number, string FileName, string Path, int PageNumber, float Score, string Excerpt);

/// <summary>
/// The "retrieve then generate" half of RAG: find the chunks that best match the question,
/// build a prompt around them, and stream the model's answer back.
/// </summary>
public sealed class RagService(OllamaClient ollama, VectorStoreProvider stores, IOptions<RagOptions> options)
{
    private readonly RagOptions _options = options.Value;

    private const string SystemPrompt =
        """
        You answer questions about a specific set of documents.

        Rules:
        - Use ONLY the numbered context passages provided. Do not use outside knowledge.
        - Cite the passages you relied on inline, like [1] or [2][3], right after the claim they support.
        - If the passages do not contain the answer, say so plainly and state what is missing.
          Do not guess, and do not pad the answer with general knowledge.
        - Quote exact figures, names and dates from the passages rather than paraphrasing them.
        - Be concise and concrete. Prefer short paragraphs or bullets.
        """;

    /// <summary>
    /// Runs retrieval and returns the passages that will be used as context. A null
    /// <paramref name="folder"/> searches every folder in the dataset.
    /// </summary>
    public async Task<List<Citation>> RetrieveAsync(
        string datasetId,
        string? folder,
        string question,
        CancellationToken ct = default)
    {
        var store = stores.Activate(datasetId);
        var folderKey = folder is null ? null : VectorStore.NormalizeFolder(folder);

        // The question is embedded with the *query* prefix while documents used the *document*
        // prefix — that asymmetry is how arctic-embed was trained, and it improves matching.
        var queryEmbedding = await ollama.EmbedSingleAsync(_options.EmbedQueryPrefix + question, ct);

        // Vectors from two different embedding models are not comparable — every score would come
        // out as noise, or the widths would not even match. Caught here so the answer is "this
        // index was built with a different model" rather than a silent "nothing found".
        var indexed = store.GetEmbeddingDimension(folderKey);
        if (indexed > 0 && indexed != queryEmbedding.Length)
        {
            throw new InvalidOperationException(
                $"This index stores {indexed}-dimension vectors, but {_options.EmbeddingModel} produces " +
                $"{queryEmbedding.Length}. It was built with a different embedding model — either switch " +
                "back to that model, or rebuild the dataset from scratch with this one.");
        }

        var hits = store.Search(folderKey, queryEmbedding, _options.TopK, _options.MinScore);

        return hits
            .Select((hit, i) => new Citation(
                Number: i + 1,
                FileName: hit.FileName,
                Path: hit.Path,
                PageNumber: hit.PageNumber,
                Score: hit.Score,
                Excerpt: hit.Text))
            .ToList();
    }

    /// <summary>Streams the answer for a question, given the passages retrieved for it.</summary>
    public IAsyncEnumerable<string> AnswerStreamAsync(
        string question,
        IReadOnlyList<Citation> citations,
        CancellationToken ct = default)
    {
        if (citations.Count == 0) return NoContextAsync();

        var prompt = BuildPrompt(question, citations);
        return ollama.ChatStreamAsync(SystemPrompt, prompt, ct);
    }

    private static async IAsyncEnumerable<string> NoContextAsync()
    {
        yield return "I couldn't find anything in the indexed documents that relates to that question. "
                   + "Try rephrasing it, or check that the folder has been indexed.";
        await Task.CompletedTask;
    }

    private static string BuildPrompt(string question, IReadOnlyList<Citation> citations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Context passages:").AppendLine();

        foreach (var citation in citations)
        {
            builder.AppendLine($"[{citation.Number}] {citation.FileName} (page {citation.PageNumber})");
            builder.AppendLine(citation.Excerpt);
            builder.AppendLine();
        }

        builder.AppendLine("---").AppendLine();
        builder.AppendLine($"Question: {question}");
        builder.AppendLine();
        builder.Append("Answer using only the passages above, citing them inline as [n].");

        return builder.ToString();
    }
}
