using SqliteWriterBench.Article;
using SqliteWriterBench.Benchmark;
using SqliteWriterBench.Engines;

string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
string repoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();
string resultsDir = Path.Combine(repoRoot, "results");
string articlePath = Path.Combine(repoRoot, "article", "substack-draft.md");

switch (command)
{
    case "run":
        await RunBenchAsync();
        break;
    case "render":
        await RenderArticleAsync();
        break;
    case "help":
    case "-h":
    case "--help":
        PrintHelp();
        break;
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
}

return 0;

async Task RunBenchAsync()
{
    string[] runArgs = [.. args.Skip(1)];
    bool matrix = runArgs.Any(a => a is "--matrix");
    BenchOptions shared = ParseOptions(runArgs.Where(a => a is not "--matrix").ToArray(), resultsDir, repoRoot);

    IDbEngine[] engines =
    [
        new SqliteEngine(),
        new SqliteWalEngine(),
        new TursoEngine(),
        new MultiwriterEngine()
    ];

    BenchOptions[] profiles = matrix
        ? BenchProfiles.BuildMatrix(shared, repoRoot)
        : [shared];

    List<BenchSuiteResult> suites = [];
    LibraryVersions? versions = null;
    foreach (BenchOptions profile in profiles)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"######## {profile.ProfileTitle} ########");
        BenchRunner runner = new BenchRunner(profile);
        BenchSuiteResult suite = await runner.RunAllAsync(engines, CancellationToken.None);
        suites.Add(suite);
        versions = suite.Versions;
    }

    BenchReport report = new BenchReport
    {
        Timestamp = DateTimeOffset.UtcNow,
        Versions = versions ?? EnvironmentInfo.CaptureLibraryVersions(),
        Profiles = suites,
    };
    await BenchRunner.WriteReportAsync(report, shared.ResultsDir);

    Directory.CreateDirectory(Path.GetDirectoryName(articlePath)!);
    await File.WriteAllTextAsync(articlePath, ArticleRenderer.Render(report));
    Console.Error.WriteLine($"Updated {articlePath}");
}

async Task RenderArticleAsync()
{
    BenchReport report = await BenchRunner.LoadLatestReportAsync(resultsDir);
    Directory.CreateDirectory(Path.GetDirectoryName(articlePath)!);
    await File.WriteAllTextAsync(articlePath, ArticleRenderer.Render(report));
    Console.Error.WriteLine($"Updated {articlePath}");
}

static BenchOptions ParseOptions(string[] args, string defaultResultsDir, string repoRoot)
{
    double duration = 8.0;
    int readers = 4;
    int rows = 100;
    int[] writers = [1, 4, 16];
    string? filter = null;
    string results = defaultResultsDir;
    string? workDir = null;
    string sync = "FULL";

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--duration" when i + 1 < args.Length:
                duration = double.Parse(args[++i]);
                break;
            case "--readers" when i + 1 < args.Length:
                readers = int.Parse(args[++i]);
                break;
            case "--rows" when i + 1 < args.Length:
                rows = int.Parse(args[++i]);
                break;
            case "--writers" when i + 1 < args.Length:
                writers =
                [
                    .. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse)
                ];
                break;
            case "--engine" when i + 1 < args.Length:
                filter = args[++i];
                break;
            case "--results" when i + 1 < args.Length:
                results = args[++i];
                break;
            case "--workdir" when i + 1 < args.Length:
                workDir = args[++i];
                break;
            case "--sync" when i + 1 < args.Length:
                sync = args[++i];
                break;
        }
    }

    string normalized = SqliteEngine.NormalizeSync(sync);
    bool durableDisk = normalized == "FULL"
        && (workDir is null || !workDir.StartsWith("/tmp", StringComparison.Ordinal));

    return new BenchOptions
    {
        Duration = TimeSpan.FromSeconds(duration),
        ReaderCount = readers,
        RowsPerTransaction = rows,
        WriterCounts = writers,
        EnginesFilter = filter,
        ResultsDir = results,
        WorkDirectory = workDir,
        Synchronous = normalized,
        ProfileId = durableDisk ? "durable-full" : $"custom-{normalized.ToLowerInvariant()}",
        ProfileTitle = durableDisk
            ? "Durable disk (synchronous=FULL)"
            : $"Custom ({normalized}" + (workDir is null ? "" : $", {workDir}") + ")",
        ProfileSummary = durableDisk
            ? "On-disk database with synchronous=FULL — every commit waits for durable sync."
            : $"synchronous={normalized}" + (workDir is null ? "" : $" · workdir={workDir}"),
    };
}

static void PrintHelp()
{
    Console.WriteLine("""
        SqliteWriterBench — compare SQLite / WAL / Turso MVCC / sqlite-multiwriter

        Usage:
          dotnet run --project src/SqliteWriterBench -- run [options]
          dotnet run --project src/SqliteWriterBench -- run --matrix [options]
          dotnet run --project src/SqliteWriterBench -- render

        Options for run:
          --duration <sec>     Default 8
          --writers 1,4,16     Writer thread counts
          --readers <n>        Concurrent readers (default 4)
          --rows <n>           Inserts per write transaction (default 100)
          --engine <substr>    Only engines whose name contains substr
          --results <dir>      Output directory for JSON
          --workdir <dir>      DB files root (default: on-disk ./tmp)
          --sync FULL|NORMAL|OFF   PRAGMA synchronous (default FULL)
          --matrix             Run durable FULL + tmpfs FULL + disk OFF profiles

        Prerequisites:
          ./scripts/fetch-multiwriter.sh
        """);
}

static string? FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "scripts", "fetch-multiwriter.sh")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "scripts", "fetch-multiwriter.sh")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}
