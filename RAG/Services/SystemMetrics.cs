using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RAG.Services;

public sealed record LoadedModel(string Name, long SizeBytes, long VramBytes, double PercentOnGpu);

public sealed record GpuInfo(string Name, long TotalBytes, long UsedBytes);

public sealed record MetricsSnapshot(
    long ProcessRamBytes,
    long ManagedHeapBytes,
    long VectorCacheBytes,
    long SystemRamTotalBytes,
    long SystemRamAvailableBytes,
    double ProcessCpuPercent,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<LoadedModel> Models,
    string? Note);

/// <summary>
/// Cheap health numbers for the dashboard: how much memory this app is using, how much the
/// machine has left, and — the one that actually predicts answer speed — how much of each Ollama
/// model made it onto the GPU.
/// </summary>
public sealed class SystemMetrics(IHttpClientFactory httpFactory, VectorStoreProvider stores, ILogger<SystemMetrics> logger)
{
    public const string HttpClientName = "ollama-metrics";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _cpuGate = new();
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc;

    // nvidia-smi costs a process launch, so its answer is reused for a few seconds.
    private readonly Lock _gpuGate = new();
    private IReadOnlyList<GpuInfo> _cachedGpus = [];
    private DateTime _gpuSampledUtc = DateTime.MinValue;

    public async Task<MetricsSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var process = Process.GetCurrentProcess();
        var (total, available) = GetSystemMemory();
        var models = await GetLoadedModelsAsync(ct);

        return new MetricsSnapshot(
            ProcessRamBytes: process.WorkingSet64,
            ManagedHeapBytes: GC.GetTotalMemory(false),
            VectorCacheBytes: stores.CachedVectorBytes,
            SystemRamTotalBytes: total,
            SystemRamAvailableBytes: available,
            ProcessCpuPercent: SampleCpuPercent(process),
            Gpus: GetGpus(),
            Models: models,
            Note: BuildNote(models, available));
    }

    /// <summary>
    /// CPU use since the previous call. A single reading is meaningless — it has to be a delta of
    /// processor time over wall-clock time, divided by the core count.
    /// </summary>
    private double SampleCpuPercent(Process process)
    {
        lock (_cpuGate)
        {
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            if (_lastCpuSampleUtc == default)
            {
                _lastCpuSampleUtc = now;
                _lastCpuTime = cpuTime;
                return 0;
            }

            var elapsed = (now - _lastCpuSampleUtc).TotalMilliseconds;
            var used = (cpuTime - _lastCpuTime).TotalMilliseconds;

            _lastCpuSampleUtc = now;
            _lastCpuTime = cpuTime;

            if (elapsed <= 0) return 0;
            return Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
        }
    }

    private static (long Total, long Available) GetSystemMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
        }

        // Fallback: the runtime knows the total, and its idea of "load" is the next best thing.
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }

    /// <summary>
    /// Asks Ollama which models are resident and how much of each sits in VRAM. When
    /// <c>size_vram</c> is below <c>size</c>, the remainder is running on the CPU, which is
    /// typically several times slower per token.
    /// </summary>
    private async Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken ct)
    {
        try
        {
            using var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync("/api/ps", ct);
            if (!response.IsSuccessStatusCode) return [];

            var payload = await response.Content.ReadFromJsonAsync<PsResponse>(Json, ct);

            return payload?.Models?.Select(m => new LoadedModel(
                m.Name ?? "(unknown)",
                m.Size,
                m.SizeVram,
                m.Size <= 0 ? 0 : Math.Round(100.0 * m.SizeVram / m.Size, 1))).ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read Ollama /api/ps");
            return [];
        }
    }

    private IReadOnlyList<GpuInfo> GetGpus()
    {
        lock (_gpuGate)
        {
            if ((DateTime.UtcNow - _gpuSampledUtc).TotalSeconds < 5) return _cachedGpus;
            _gpuSampledUtc = DateTime.UtcNow;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null) return _cachedGpus = [];

                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(2000)) return _cachedGpus = [];

                var gpus = new List<GpuInfo>();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length < 3) continue;
                    if (!long.TryParse(parts[1], out var totalMb) || !long.TryParse(parts[2], out var usedMb)) continue;

                    gpus.Add(new GpuInfo(parts[0], totalMb * 1024 * 1024, usedMb * 1024 * 1024));
                }

                return _cachedGpus = gpus;
            }
            catch
            {
                // No NVIDIA driver, or an AMD/Intel machine. GPU stats are a bonus, not a requirement.
                return _cachedGpus = [];
            }
        }
    }

    private static string? BuildNote(IReadOnlyList<LoadedModel> models, long availableRam)
    {
        var spilled = models.FirstOrDefault(m => m.PercentOnGpu < 99 && m.SizeBytes > 0);
        if (spilled is not null)
        {
            return $"{spilled.Name} is only {spilled.PercentOnGpu:0.#}% on the GPU — the rest runs on CPU, " +
                   "which is the main reason answers are slow. A smaller model or quantisation would fit fully in VRAM.";
        }

        if (availableRam is > 0 and < 2L * 1024 * 1024 * 1024)
            return "Less than 2 GB of system RAM free — indexing a large folder may struggle.";

        return null;
    }

    private sealed class PsResponse
    {
        public List<PsModel>? Models { get; set; }
    }

    private sealed class PsModel
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("size_vram")]
        public long SizeVram { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
