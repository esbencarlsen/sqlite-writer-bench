using System.Text.Json.Serialization;

namespace SqliteWriterBench.Benchmark;

public sealed class BenchRunResult
{
    public required string Engine { get; init; }
    public required int Writers { get; init; }
    public required int Readers { get; init; }
    public required double DurationSeconds { get; init; }
    public required long WriteTransactions { get; init; }
    public required long WriteRetries { get; init; }
    public required long WriteErrors { get; init; }
    public required long ReadOperations { get; init; }
    public required long ReadErrors { get; init; }
    public required double WriteTxPerSecond { get; init; }
    public required double ReadOpsPerSecond { get; init; }
    public required double RetriesPer100Tx { get; init; }
    public required LatencyStats WriteLatencyMs { get; init; }
}

public sealed class LatencyStats
{
    public required double P50 { get; init; }
    public required double P99 { get; init; }
    public required double P999 { get; init; }
    public required double Max { get; init; }

    public static LatencyStats FromSorted(List<double> sortedMs)
    {
        if (sortedMs.Count == 0)
        {
            return new LatencyStats { P50 = 0, P99 = 0, P999 = 0, Max = 0 };
        }

        return new LatencyStats
        {
            P50 = Percentile(sortedMs, 0.50),
            P99 = Percentile(sortedMs, 0.99),
            P999 = Percentile(sortedMs, 0.999),
            Max = sortedMs[^1],
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        int index = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

public sealed class BenchReport
{
    public required DateTimeOffset Timestamp { get; init; }
    public required LibraryVersions Versions { get; init; }
    public required List<BenchSuiteResult> Profiles { get; init; }
}

public sealed class BenchSuiteResult
{
    public required DateTimeOffset Timestamp { get; init; }
    public string ProfileId { get; init; } = "default";
    public string ProfileTitle { get; init; } = "Benchmark profile";
    public string ProfileSummary { get; init; } = "";
    public required HardwareInfo Hardware { get; init; }
    public required LibraryVersions Versions { get; init; }
    public required BenchOptionsSnapshot Options { get; init; }
    public required List<BenchRunResult> Runs { get; init; }
}

public sealed class HardwareInfo
{
    public required string CpuModel { get; init; }
    public required int ProcessorCount { get; init; }
    public required string OsArch { get; init; }
    public required string WorkDirectory { get; init; }
    public required string FileSystem { get; init; }
}

public sealed class LibraryVersions
{
    public required string Sqlite { get; init; }
    public required string MicrosoftDataSqlite { get; init; }
    public required string TursoPackage { get; init; }
    public string? TursoEngine { get; init; }
    public required string SqliteMultiwriter { get; init; }
    public required string DotNet { get; init; }
}

public sealed class BenchOptionsSnapshot
{
    public required double DurationSeconds { get; init; }
    public required int[] WriterCounts { get; init; }
    public required int ReaderCount { get; init; }
    public required int RowsPerTransaction { get; init; }
    public required int PayloadBytes { get; init; }
    public required string Durability { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}
