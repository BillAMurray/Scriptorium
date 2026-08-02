using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RAG.Services;

public enum CopyState { Idle, Running, Completed, Failed }

public sealed record CopyStatus(CopyState State, string? Source, string? Target, int PercentComplete, string? Error);

/// <summary>
/// The library: every dataset is a folder under the datasets root holding its own SQLite index and
/// a <c>dataset.json</c>.
///
/// One folder per dataset rather than one big database with a name column, because it makes the
/// unit of the library the same as the unit of the file system. Backing one up, moving it to
/// another machine or throwing it away is a folder operation, and a problem with one index cannot
/// reach the others.
/// </summary>
public sealed class DatasetRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string MetadataFileName = "dataset.json";

    private readonly string _databaseFileName;
    private readonly ILogger<DatasetRegistry> _logger;
    private readonly Lock _gate = new();

    // Duplicating a multi-gigabyte index takes long enough that the browser would time out waiting,
    // so it runs in the background and the page polls this the same way it polls indexing.
    private CopyState _copyState = CopyState.Idle;
    private string? _copySource, _copyTarget, _copyError;
    private int _copyPercent;

    public DatasetRegistry(IOptions<RagOptions> options, IHostEnvironment environment, ILogger<DatasetRegistry> logger)
    {
        _logger = logger;
        _databaseFileName = options.Value.DatabaseFile;

        var root = options.Value.DataSetsRoot;
        if (!Path.IsPathRooted(root)) root = Path.Combine(environment.ContentRootPath, root);

        RootPath = Path.GetFullPath(root);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    /// <summary>
    /// Folders starting with an underscore are working space — quarantined journals, backups and
    /// deleted datasets — and are deliberately not offered as datasets.
    /// </summary>
    private static bool IsLibraryFolder(string name) => !name.StartsWith('_') && !name.StartsWith('.');

    public string DirectoryFor(string id) => Path.Combine(RootPath, id);

    public string DatabasePathFor(string id) => Path.Combine(RootPath, id, _databaseFileName);

    public List<Dataset> List()
    {
        var datasets = new List<Dataset>();

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            var id = Path.GetFileName(directory);
            if (!IsLibraryFolder(id)) continue;

            // A folder counts as a dataset if it holds either half of one, so indexes created
            // before the library existed are picked up without needing to be imported.
            var databasePath = Path.Combine(directory, _databaseFileName);
            if (!File.Exists(databasePath) && !File.Exists(Path.Combine(directory, MetadataFileName))) continue;

            datasets.Add(Load(id));
        }

        return datasets.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Exists(string id) => Directory.Exists(DirectoryFor(id));

    public Dataset Load(string id)
    {
        var directory = DirectoryFor(id);
        var databasePath = DatabasePathFor(id);
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var metadata = ReadMetadata(directory);

        var file = new FileInfo(databasePath);

        // An index adopted from before the library existed has no metadata, so the folder's own
        // creation time is a better answer than "now".
        var created = File.Exists(metadataPath)
            ? metadata.CreatedUtc
            : Directory.GetCreationTimeUtc(directory);

        return new Dataset(
            Id: id,
            DisplayName: string.IsNullOrWhiteSpace(metadata.DisplayName) ? id : metadata.DisplayName,
            Description: metadata.Description,
            DirectoryPath: directory,
            DatabasePath: databasePath,
            EmbeddingModel: metadata.EmbeddingModel,
            EmbeddingDimension: metadata.EmbeddingDimension,
            DocumentCount: metadata.DocumentCount,
            ChunkCount: metadata.ChunkCount,
            CountsKnown: File.Exists(metadataPath),
            CreatedUtc: created,
            LastIndexedUtc: metadata.LastIndexedUtc,
            DatabaseBytes: file.Exists ? file.Length : 0);
    }

    /// <summary>Loads a dataset together with the health of its database file.</summary>
    public Dataset LoadWithInspection(string id)
        => Load(id) with { Inspection = DatabaseGuard.Inspect(DatabasePathFor(id)) };

    public Dataset Create(string displayName, string description = "")
    {
        var id = MakeId(displayName);
        if (id.Length == 0) throw new ArgumentException("Give the dataset a name.");

        lock (_gate)
        {
            if (Directory.Exists(DirectoryFor(id)))
                throw new InvalidOperationException($"A dataset called \"{displayName}\" already exists.");

            Directory.CreateDirectory(DirectoryFor(id));
            WriteMetadata(DirectoryFor(id), new DatasetMetadata
            {
                DisplayName = displayName.Trim(),
                Description = description.Trim(),
                CreatedUtc = DateTime.UtcNow
            });
        }

        _logger.LogInformation("Created dataset {Id} at {Path}", id, DirectoryFor(id));
        return Load(id);
    }

    /// <summary>
    /// Renames the dataset's label only. The folder name stays put: it is the identity the index,
    /// any open connection and the browser's saved selection all hang off, and a display name is
    /// not worth breaking those for.
    /// </summary>
    public Dataset Rename(string id, string displayName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Give the dataset a name.");
        RequireExists(id);

        var metadata = ReadMetadata(DirectoryFor(id));
        metadata.DisplayName = displayName.Trim();
        if (description is not null) metadata.Description = description.Trim();

        WriteMetadata(DirectoryFor(id), metadata);
        return Load(id);
    }

    /// <summary>
    /// Retires a dataset by moving it to a timestamped <c>_deleted-…</c> folder rather than erasing
    /// it. A rename within the same volume is instant however large the index is, and it means a
    /// misclick costs a file move instead of a re-index that took hours.
    /// </summary>
    public string Delete(string id, DateTime stampUtc)
    {
        RequireExists(id);

        var target = Path.Combine(RootPath, $"_deleted-{id}-{stampUtc:yyyyMMdd-HHmmss}");
        Directory.Move(DirectoryFor(id), target);

        _logger.LogInformation("Dataset {Id} moved to {Target}", id, target);
        return target;
    }

    public void StartDuplicate(string sourceId, string newDisplayName)
    {
        RequireExists(sourceId);

        var targetId = MakeId(newDisplayName);
        if (targetId.Length == 0) throw new ArgumentException("Give the copy a name.");

        lock (_gate)
        {
            if (_copyState == CopyState.Running)
                throw new InvalidOperationException("A dataset copy is already running.");

            if (Directory.Exists(DirectoryFor(targetId)))
                throw new InvalidOperationException($"A dataset called \"{newDisplayName}\" already exists.");

            _copyState = CopyState.Running;
            _copySource = sourceId;
            _copyTarget = targetId;
            _copyPercent = 0;
            _copyError = null;
        }

        _ = Task.Run(() => DuplicateAsync(sourceId, targetId, newDisplayName.Trim()));
    }

    private async Task DuplicateAsync(string sourceId, string targetId, string displayName)
    {
        var targetDirectory = DirectoryFor(targetId);

        try
        {
            Directory.CreateDirectory(targetDirectory);

            var source = DatabasePathFor(sourceId);
            if (File.Exists(source))
            {
                await CopyWithProgressAsync(source, DatabasePathFor(targetId));
            }

            var metadata = ReadMetadata(DirectoryFor(sourceId));
            metadata.DisplayName = displayName;
            metadata.CreatedUtc = DateTime.UtcNow;
            WriteMetadata(targetDirectory, metadata);

            lock (_gate)
            {
                _copyState = CopyState.Completed;
                _copyPercent = 100;
            }

            _logger.LogInformation("Duplicated dataset {Source} to {Target}", sourceId, targetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to duplicate dataset {Source}", sourceId);

            // A half-written copy is worse than none: it would show up in the library as a real
            // dataset and fail confusingly on first use.
            try { if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: true); }
            catch (Exception cleanup) { _logger.LogWarning(cleanup, "Could not clean up {Target}", targetDirectory); }

            lock (_gate)
            {
                _copyState = CopyState.Failed;
                _copyError = ex.Message;
            }
        }
    }

    private async Task CopyWithProgressAsync(string source, string target)
    {
        const int bufferSize = 1024 * 1024;

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        var buffer = new byte[bufferSize];
        long copied = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read));
            copied += read;

            var percent = input.Length == 0 ? 100 : (int)(100L * copied / input.Length);
            lock (_gate) _copyPercent = percent;
        }
    }

    public CopyStatus GetCopyStatus()
    {
        lock (_gate) return new CopyStatus(_copyState, _copySource, _copyTarget, _copyPercent, _copyError);
    }

    /// <summary>Records what a finished indexing run tells us about the dataset.</summary>
    public void NoteIndexed(string id, string embeddingModel, int dimension, FolderStats totals)
    {
        if (!Exists(id)) return;

        lock (_gate)
        {
            var metadata = ReadMetadata(DirectoryFor(id));
            metadata.LastIndexedUtc = DateTime.UtcNow;
            metadata.DocumentCount = totals.DocumentCount;
            metadata.ChunkCount = totals.ChunkCount;
            if (!string.IsNullOrWhiteSpace(embeddingModel)) metadata.EmbeddingModel = embeddingModel;
            if (dimension > 0) metadata.EmbeddingDimension = dimension;
            WriteMetadata(DirectoryFor(id), metadata);
        }
    }

    /// <summary>
    /// Refreshes the cached counts for a dataset that is already open. Used when a dataset is first
    /// selected, so indexes built before the library existed pick up real numbers without waiting
    /// for the next indexing run.
    /// </summary>
    public void NoteOpened(string id, int dimension, FolderStats totals)
    {
        if (!Exists(id)) return;

        lock (_gate)
        {
            var metadata = ReadMetadata(DirectoryFor(id));

            if (metadata.DocumentCount == totals.DocumentCount
                && metadata.ChunkCount == totals.ChunkCount
                && (dimension == 0 || metadata.EmbeddingDimension == dimension))
            {
                return;
            }

            metadata.DocumentCount = totals.DocumentCount;
            metadata.ChunkCount = totals.ChunkCount;
            metadata.LastIndexedUtc ??= totals.LastIndexedUtc;
            if (dimension > 0) metadata.EmbeddingDimension = dimension;
            WriteMetadata(DirectoryFor(id), metadata);
        }
    }

    private void RequireExists(string id)
    {
        if (!Exists(id)) throw new DirectoryNotFoundException($"No dataset called \"{id}\".");
    }

    private static DatasetMetadata ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(path)) return new DatasetMetadata();

        try
        {
            return JsonSerializer.Deserialize<DatasetMetadata>(File.ReadAllText(path), Json) ?? new DatasetMetadata();
        }
        catch (JsonException)
        {
            // A damaged metadata file must not hide the index it sits next to.
            return new DatasetMetadata();
        }
    }

    private static void WriteMetadata(string directory, DatasetMetadata metadata)
        => File.WriteAllText(Path.Combine(directory, MetadataFileName), JsonSerializer.Serialize(metadata, Json));

    /// <summary>
    /// Turns a display name into a folder name. Conservative on purpose — the result becomes a
    /// path, so anything that is not plainly safe is replaced rather than escaped.
    /// </summary>
    private static string MakeId(string displayName)
    {
        var cleaned = new string(displayName.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '-')
            .ToArray())
            .Replace(' ', '-');

        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");

        return cleaned.Trim('-', '_', '.');
    }
}
