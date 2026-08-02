using System.Text.Json.Serialization;

namespace RAG.Services;

/// <summary>
/// The bits of a dataset that are worth writing down rather than recomputing. Lives in
/// <c>dataset.json</c> beside the database, so a dataset folder is self-describing: copy it to
/// another machine and it arrives with its name and its provenance intact.
/// </summary>
public sealed class DatasetMetadata
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// The embedding model the index was built with. Vectors from two different models are not
    /// comparable, so a query embedded with the wrong one silently matches nothing — recording it
    /// is what lets the app say so instead.
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    public int EmbeddingDimension { get; set; }

    /// <summary>
    /// Counts as of the last indexing run. Cached here so the library list can show the size of
    /// every dataset without opening every database — which would mean a connection, a schema
    /// check and a fresh journal for each one, just to draw a list.
    /// </summary>
    public int DocumentCount { get; set; }

    public int ChunkCount { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastIndexedUtc { get; set; }
}

/// <summary>A dataset as the rest of the app sees it: metadata plus where it lives on disk.</summary>
public sealed record Dataset(
    string Id,
    string DisplayName,
    string Description,
    string DirectoryPath,
    string DatabasePath,
    string EmbeddingModel,
    int EmbeddingDimension,
    int DocumentCount,
    int ChunkCount,

    /// <summary>
    /// False for a folder that has an index but no dataset.json yet — an index made before the
    /// library existed. Its counts are simply not known until it is opened, which is different
    /// from being known to be zero.
    /// </summary>
    bool CountsKnown,

    DateTime CreatedUtc,
    DateTime? LastIndexedUtc,
    long DatabaseBytes)
{
    /// <summary>Filled in by the registry only when asked, since it means opening the database.</summary>
    [JsonIgnore]
    public DatabaseInspection? Inspection { get; init; }
}

/// <summary>Per-folder rollup shown in the dataset's contents list.</summary>
public sealed record DatasetFolder(string Folder, int DocumentCount, int ChunkCount, DateTime? LastIndexedUtc);
