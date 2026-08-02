using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RAG.Services;

/// <summary>
/// A thin wrapper over the two Ollama HTTP endpoints this app needs:
/// POST /api/embed (text -> vector) and POST /api/chat (prompt -> streamed answer).
/// Deliberately hand-rolled rather than using a client library, so the wire format is visible.
/// </summary>
public sealed class OllamaClient(HttpClient http, IOptions<RagOptions> options, ILogger<OllamaClient> logger)
{
    private readonly RagOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Turns a batch of strings into vectors. Ollama accepts an array of inputs and returns
    /// an array of embeddings in the same order, which is much faster than one call per chunk.
    /// </summary>
    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return [];

        var request = new { model = _options.EmbeddingModel, input = inputs };

        using var response = await http.PostAsJsonAsync("/api/embed", request, Json, ct);
        await ThrowIfFailedAsync(response, "embed", ct);

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(Json, ct)
                      ?? throw new InvalidOperationException("Ollama returned an empty embedding response.");

        if (payload.Embeddings is null || payload.Embeddings.Count != inputs.Count)
        {
            throw new InvalidOperationException(
                $"Expected {inputs.Count} embeddings from Ollama but got {payload.Embeddings?.Count ?? 0}.");
        }

        // Normalising to unit length here means a cosine similarity later is just a dot product.
        foreach (var vector in payload.Embeddings) Normalize(vector);
        return payload.Embeddings;
    }

    public async Task<float[]> EmbedSingleAsync(string input, CancellationToken ct = default)
        => (await EmbedAsync([input], ct))[0];

    /// <summary>
    /// Streams the assistant's reply token by token. Ollama responds with newline-delimited JSON,
    /// one object per token, so we read the body as it arrives rather than buffering the whole thing.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new
        {
            model = _options.ChatModel,
            stream = true,
            think = _options.Think ? true : (bool?)false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            options = new
            {
                temperature = _options.Temperature,
                num_ctx = _options.NumCtx
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request, options: Json)
        };

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfFailedAsync(response, "chat", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) continue;

            ChatChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatChunk>(line, Json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping unparseable line from Ollama: {Line}", line);
                continue;
            }

            if (chunk?.Error is { Length: > 0 })
                throw new InvalidOperationException($"Ollama error: {chunk.Error}");

            var content = chunk?.Message?.Content;
            if (!string.IsNullOrEmpty(content)) yield return content;

            if (chunk?.Done == true) yield break;
        }
    }

    /// <summary>Checks Ollama is reachable and reports which models it currently has pulled.</summary>
    public async Task<(bool Reachable, string[] Models, string? Error)> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return (false, [], $"Ollama replied {(int)response.StatusCode} {response.ReasonPhrase}.");

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(Json, ct);
            var models = tags?.Models?.Select(m => m.Name).Where(n => n is not null).Select(n => n!).ToArray() ?? [];
            return (true, models, null);
        }
        catch (Exception ex)
        {
            return (false, [], $"Could not reach Ollama at {_options.OllamaBaseUrl}: {ex.Message}");
        }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Ollama /{what} failed with {(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim());
    }

    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var value in vector) sumOfSquares += value * value;

        var magnitude = Math.Sqrt(sumOfSquares);
        if (magnitude < 1e-12) return;

        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / magnitude);
    }

    private sealed class EmbedResponse
    {
        public List<float[]>? Embeddings { get; set; }
    }

    private sealed class ChatChunk
    {
        public ChatMessage? Message { get; set; }
        public bool Done { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? Content { get; set; }
        public string? Thinking { get; set; }
    }

    private sealed class TagsResponse
    {
        public List<TagEntry>? Models { get; set; }
    }

    private sealed class TagEntry
    {
        public string? Name { get; set; }
    }
}
