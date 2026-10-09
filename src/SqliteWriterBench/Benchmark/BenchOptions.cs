namespace SqliteWriterBench.Benchmark;

public sealed class BenchOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(8);
    public int[] WriterCounts { get; init; } = [1, 4, 16];
    public int ReaderCount { get; init; } = 4;
    public int RowsPerTransaction { get; init; } = 100;
    public int PayloadBytes { get; init; } = 64;
    public int MaxRetries { get; init; } = 1000;
    public string ResultsDir { get; init; } = "results";
    public string? EnginesFilter { get; init; }

    /// <summary>
    /// Directory for per-cell databases. Defaults to on-disk project tmp/ (not tmpfs),
    /// so synchronous=FULL reflects durable storage.
    /// </summary>
    public string? WorkDirectory { get; init; }

    /// <summary>SQLite PRAGMA synchronous: FULL, NORMAL, or OFF.</summary>
    public string Synchronous { get; init; } = "FULL";

    public string ProfileId { get; init; } = "durable-full";
    public string ProfileTitle { get; init; } = "Durable disk (synchronous=FULL)";
    public string ProfileSummary { get; init; } =
        "On-disk database with synchronous=FULL — every commit waits for durable sync.";
}

public static class BenchProfiles
{
    public static BenchOptions[] BuildMatrix(BenchOptions shared, string repoRoot)
    {
        string diskTmp = Path.Combine(repoRoot, "tmp");
        string tmpfsRoot = Path.Combine(Path.GetTempPath(), "sqlite-writer-bench-matrix");

        return
        [
            Clone(shared, "durable-full",
                "Durable disk (synchronous=FULL)",
                "On-disk database with synchronous=FULL — production-shaped durability; every commit waits for sync.",
                diskTmp, "FULL"),
            Clone(shared, "tmpfs-full",
                "tmpfs / non-durable (synchronous=FULL)",
                "Database on Linux tmpfs (RAM). FULL still runs, but sync is essentially free — not crash-safe storage.",
                tmpfsRoot, "FULL"),
            Clone(shared, "disk-off",
                "Disk without fsync (synchronous=OFF)",
                "On-disk database with synchronous=OFF — OS may buffer writes; much faster, not durable across power loss.",
                diskTmp, "OFF"),
        ];
    }

    private static BenchOptions Clone(
        BenchOptions shared,
        string id,
        string title,
        string summary,
        string workDir,
        string sync) =>
        new()
        {
            Duration = shared.Duration,
            WriterCounts = shared.WriterCounts,
            ReaderCount = shared.ReaderCount,
            RowsPerTransaction = shared.RowsPerTransaction,
            PayloadBytes = shared.PayloadBytes,
            MaxRetries = shared.MaxRetries,
            ResultsDir = shared.ResultsDir,
            EnginesFilter = shared.EnginesFilter,
            WorkDirectory = workDir,
            Synchronous = sync,
            ProfileId = id,
            ProfileTitle = title,
            ProfileSummary = summary,
        };
}
