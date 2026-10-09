using System.Globalization;
using System.Text;
using SqliteWriterBench.Benchmark;

namespace SqliteWriterBench.Article;

public static class ArticleRenderer
{
    public static string Render(BenchSuiteResult suite) =>
        Render(new BenchReport
        {
            Timestamp = suite.Timestamp,
            Versions = suite.Versions,
            Profiles = [suite],
        });

    public static string Render(BenchReport report)
    {
        StringBuilder sb = new StringBuilder();
        CultureInfo culture = CultureInfo.InvariantCulture;
        BenchSuiteResult primary = report.Profiles[0];

        sb.AppendLine("SQLite vs WAL vs Turso MVCC vs sqlite-multiwriter");
        sb.AppendLine();
        sb.AppendLine("### A hands-on read/write bake-off after Marco Bambini’s multi-writer announcement");
        sb.AppendLine();
        sb.AppendLine(
            "[Marco Bambini’s post](https://marcobambini.substack.com/p/we-solved-sqlites-single-writer-limitation) "
            + "argues that SQLite’s single-writer lock is the wrong bottleneck for a world of concurrent agents — "
            + "and introduces [sqlite-multiwriter](https://github.com/sqliteai/sqlite-multiwriter), a VFS that gives "
            + "each writer a private WAL while keeping a normal SQLite file on disk.");
        sb.AppendLine();
        sb.AppendLine(
            "Turso took a different route: an SQLite-compatible rewrite with an MVCC engine "
            + "(`PRAGMA journal_mode = 'mvcc'` + `BEGIN CONCURRENT`). This note measures both approaches against "
            + "stock SQLite (DELETE journal) and SQLite in WAL mode, on the same machine, with the same workload — "
            + "**with and without durable fsync**.");
        sb.AppendLine();
        sb.AppendLine("## Method");
        sb.AppendLine();
        sb.AppendLine(
            $"- **Hardware:** {primary.Hardware.CpuModel} · {primary.Hardware.ProcessorCount} logical CPUs · {primary.Hardware.OsArch}");
        sb.AppendLine($"- **Duration:** {primary.Options.DurationSeconds:0.#}s per (engine × writer-count) cell, per durability profile");
        sb.AppendLine($"- **Writers:** {string.Join(", ", primary.Options.WriterCounts)} threads (one connection each)");
        sb.AppendLine(
            $"- **Readers:** {primary.Options.ReaderCount} threads concurrently running `MAX(id)` / indexed recent-row lookups");
        sb.AppendLine(
            $"- **Write tx:** {primary.Options.RowsPerTransaction} inserts of {primary.Options.PayloadBytes}-byte payloads (each writer’s own rows)");
        sb.AppendLine("- **Engines:** SQLite DELETE · SQLite WAL · Turso MVCC · sqlite-multiwriter (`vfs=multiwriter&mw_rebase=1`)");
        sb.AppendLine("- **`temp_store=MEMORY`** on every engine");
        sb.AppendLine("- **Durability profiles** (same workload, different sync/storage):");
        foreach (BenchSuiteResult profile in report.Profiles)
        {
            sb.AppendLine(
                $"  - **{profile.ProfileTitle}** — `{profile.Options.Durability}` on `{profile.Hardware.FileSystem}`");
        }

        AppendVersions(sb, report.Versions);
        sb.AppendLine($"- **Captured:** {report.Timestamp.ToString("u", culture)} UTC");
        sb.AppendLine();
        sb.AppendLine(
            "Fairness notes: fresh database directory per cell; ADO.NET objects are never shared across threads; "
            + "Turso is a separate engine (not a SQLite VFS), while sqlite-multiwriter loads into stock SQLite. "
            + "Stock SQLite (DELETE or WAL) still serializes writers — flat write tx/s as writer count rises is expected "
            + "when you are not fsync-bound. Conflict-heavy “same rows” workloads are out of scope here.");
        sb.AppendLine();

        foreach (BenchSuiteResult profile in report.Profiles)
        {
            AppendProfileSection(sb, profile, culture);
        }

        sb.AppendLine("## What the numbers suggest");
        sb.AppendLine();
        AppendCrossProfileInterpretation(sb, report);
        sb.AppendLine();
        AppendFsyncExplanation(sb, report);
        sb.AppendLine();
        sb.AppendLine("## Caveats");
        sb.AppendLine();
        sb.AppendLine("- **Hot-row conflicts** still serialize (or retry) on every engine; rebase / MVCC only help when writers touch different rows.");
        sb.AppendLine("- **sqlite-multiwriter** needs a local filesystem and the native extension; the DB is only fully compacted when the last connection closes.");
        sb.AppendLine("- **Turso MVCC** changes the API surface (`BEGIN CONCURRENT`) and is not binary-compatible with stock `libsqlite3` extensions.");
        sb.AppendLine("- **tmpfs** and **`synchronous=OFF`** are not production durability settings; they exist here to show how much of the scoreboard is fsync.");
        sb.AppendLine("- These are single-host thread results, not the multi-process (`mw_mp=1`) matrix from the announcement.");
        sb.AppendLine();
        sb.AppendLine("## Reproduce");
        sb.AppendLine();
        sb.AppendLine(
            "Harness and instructions: "
            + "[github.com/esbencarlsen/sqlite-writer-bench](https://github.com/esbencarlsen/sqlite-writer-bench).");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("git clone https://github.com/esbencarlsen/sqlite-writer-bench.git");
        sb.AppendLine("cd sqlite-writer-bench");
        sb.AppendLine("./scripts/fetch-multiwriter.sh");
        sb.AppendLine("dotnet run --project src/SqliteWriterBench -c Release -- run --matrix");
        sb.AppendLine("dotnet run --project src/SqliteWriterBench -- render");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Paste the generated `article/substack-draft.md` into Substack as a new post.");
        sb.AppendLine();
        sb.AppendLine("## Links");
        sb.AppendLine();
        sb.AppendLine("- [esbencarlsen/sqlite-writer-bench](https://github.com/esbencarlsen/sqlite-writer-bench) (this benchmark harness)");
        sb.AppendLine("- [We solved SQLite’s single-writer limitation](https://marcobambini.substack.com/p/we-solved-sqlites-single-writer-limitation)");
        sb.AppendLine("- [sqliteai/sqlite-multiwriter](https://github.com/sqliteai/sqlite-multiwriter)");
        sb.AppendLine("- [Turso concurrent writes](https://docs.turso.tech/tursodb/concurrent-writes)");

        return sb.ToString();
    }

    private static void AppendProfileSection(StringBuilder sb, BenchSuiteResult suite, CultureInfo culture)
    {
        sb.AppendLine($"## {suite.ProfileTitle}");
        sb.AppendLine();
        sb.AppendLine(suite.ProfileSummary);
        sb.AppendLine();
        sb.AppendLine(
            $"- Storage: `{suite.Hardware.FileSystem}` · `{suite.Options.Durability}` · `temp_store=MEMORY`");
        sb.AppendLine();

        sb.AppendLine("### Write throughput (tx/s)");
        sb.AppendLine();
        AppendPivotTable(sb, suite, r => r.WriteTxPerSecond.ToString("F0", culture), "tx/s");
        sb.AppendLine();

        sb.AppendLine("### Read throughput (ops/s) while writers run");
        sb.AppendLine();
        AppendPivotTable(sb, suite, r => r.ReadOpsPerSecond.ToString("F0", culture), "ops/s");
        sb.AppendLine();

        sb.AppendLine("### Write commit latency (ms, retries included)");
        sb.AppendLine();
        sb.AppendLine("| Engine | Writers | p50 | p99 | p99.9 | max |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (BenchRunResult run in suite.Runs)
        {
            sb.AppendLine(culture,
                $"| {run.Engine} | {run.Writers} | {run.WriteLatencyMs.P50:F2} | {run.WriteLatencyMs.P99:F2} | {run.WriteLatencyMs.P999:F2} | {run.WriteLatencyMs.Max:F2} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Retries per 100 committed write transactions");
        sb.AppendLine();
        AppendPivotTable(sb, suite, r => r.RetriesPer100Tx.ToString("F1", culture), "retries");
        sb.AppendLine();
    }

    private static void AppendVersions(StringBuilder sb, LibraryVersions versions)
    {
        string turso = string.IsNullOrWhiteSpace(versions.TursoEngine)
            ? versions.TursoPackage
            : $"{versions.TursoPackage} (engine {versions.TursoEngine})";

        sb.AppendLine(
            $"- **Versions:** SQLite {versions.Sqlite}; Microsoft.Data.Sqlite {versions.MicrosoftDataSqlite}; "
            + $"Turso {turso}; sqlite-multiwriter {versions.SqliteMultiwriter}; {versions.DotNet}");
    }

    private static void AppendPivotTable(
        StringBuilder sb,
        BenchSuiteResult suite,
        Func<BenchRunResult, string> cell,
        string unit)
    {
        List<string> engines = [.. suite.Runs.Select(r => r.Engine).Distinct()];
        List<int> writers = [.. suite.Runs.Select(r => r.Writers).Distinct().OrderBy(x => x)];

        sb.Append("| Writers |");
        foreach (string engine in engines)
        {
            sb.Append($" {engine} ({unit}) |");
        }

        sb.AppendLine();
        sb.Append("|---:|");
        foreach (string _ in engines)
        {
            sb.Append("---:|");
        }

        sb.AppendLine();

        foreach (int w in writers)
        {
            sb.Append($"| {w} |");
            foreach (string engine in engines)
            {
                BenchRunResult? run = suite.Runs.FirstOrDefault(r => r.Engine == engine && r.Writers == w);
                sb.Append(run is null ? " — |" : $" {cell(run)} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendCrossProfileInterpretation(StringBuilder sb, BenchReport report)
    {
        BenchSuiteResult? durable = report.Profiles.FirstOrDefault(p => p.ProfileId.Contains("durable", StringComparison.OrdinalIgnoreCase))
            ?? report.Profiles.FirstOrDefault(p => p.Options.Durability.Contains("FULL", StringComparison.OrdinalIgnoreCase)
                && !p.Hardware.FileSystem.Contains("tmpfs", StringComparison.OrdinalIgnoreCase));
        BenchSuiteResult? tmpfs = report.Profiles.FirstOrDefault(p => p.ProfileId.Contains("tmpfs", StringComparison.OrdinalIgnoreCase)
            || p.Hardware.FileSystem.Contains("tmpfs", StringComparison.OrdinalIgnoreCase));
        BenchSuiteResult? nosync = report.Profiles.FirstOrDefault(p => p.ProfileId.Contains("off", StringComparison.OrdinalIgnoreCase)
            || p.Options.Durability.Contains("OFF", StringComparison.OrdinalIgnoreCase));

        static BenchRunResult? At(BenchSuiteResult? suite, string engine, int writers) =>
            suite?.Runs.FirstOrDefault(r => r.Engine == engine && r.Writers == writers);

        int w = report.Profiles.SelectMany(p => p.Options.WriterCounts).DefaultIfEmpty(16).Max();

        if (durable is not null)
        {
            BenchRunResult? wal = At(durable, "SQLite WAL", w);
            BenchRunResult? mw = At(durable, "sqlite-multiwriter", w);
            BenchRunResult? turso = At(durable, "Turso MVCC", w);
            if (wal is not null && mw is not null)
            {
                sb.AppendLine(
                    $"On **durable disk + FULL**, at {w} writers, WAL is about **{wal.WriteTxPerSecond:F0} tx/s** and "
                    + $"multiwriter **{mw.WriteTxPerSecond:F0} tx/s** — a modest gap, and both stay relatively flat as "
                    + "writer count rises. That is the fsync/publish ceiling.");
                sb.AppendLine();
            }

            if (turso is not null)
            {
                sb.AppendLine(
                    $"Turso MVCC on the same durable profile reaches **{turso.WriteTxPerSecond:F0} tx/s** at {w} writers "
                    + "and *does* scale with concurrency — a different engine and commit path, not a SQLite VFS.");
                sb.AppendLine();
            }
        }

        if (tmpfs is not null && durable is not null)
        {
            BenchRunResult? walDisk = At(durable, "SQLite WAL", w);
            BenchRunResult? walTmp = At(tmpfs, "SQLite WAL", w);
            BenchRunResult? mwTmp = At(tmpfs, "sqlite-multiwriter", w);
            if (walDisk is not null && walTmp is not null)
            {
                double boost = walDisk.WriteTxPerSecond <= 0 ? 0 : walTmp.WriteTxPerSecond / walDisk.WriteTxPerSecond;
                sb.AppendLine(
                    $"Move the **same** WAL+FULL workload onto **tmpfs** and WAL jumps to about "
                    + $"**{walTmp.WriteTxPerSecond:F0} tx/s** (~{boost:F0}× the durable figure)"
                    + (mwTmp is null
                        ? "."
                        : $", with multiwriter around **{mwTmp.WriteTxPerSecond:F0} tx/s**. "
                          + "On RAM-backed storage WAL often *wins*; multiwriter’s concurrency edge shrinks when sync is free."));
                sb.AppendLine();
            }
        }

        if (nosync is not null && durable is not null)
        {
            BenchRunResult? walDisk = At(durable, "SQLite WAL", w);
            BenchRunResult? walOff = At(nosync, "SQLite WAL", w);
            if (walDisk is not null && walOff is not null)
            {
                double boost = walDisk.WriteTxPerSecond <= 0 ? 0 : walOff.WriteTxPerSecond / walDisk.WriteTxPerSecond;
                sb.AppendLine(
                    $"Keep the DB on **disk** but set **`synchronous=OFF`** and WAL reaches about "
                    + $"**{walOff.WriteTxPerSecond:F0} tx/s** (~{boost:F0}× durable FULL) — proof that most of the "
                    + "durable-scoreboard cost is fsync, not SQL parsing or B-tree work.");
                sb.AppendLine();
            }
        }

        sb.AppendLine(
            "Production SQLite is normally on **real disk** with WAL and often `synchronous=NORMAL` (or FULL when you "
            + "truly need it) — not tmpfs, and not OFF, unless the data is disposable or rebuilt elsewhere.");
    }

    private static void AppendFsyncExplanation(StringBuilder sb, BenchReport report)
    {
        BenchSuiteResult? durable = report.Profiles.FirstOrDefault(p =>
            p.ProfileId.Contains("durable", StringComparison.OrdinalIgnoreCase));
        BenchSuiteResult? tmpfs = report.Profiles.FirstOrDefault(p =>
            p.ProfileId.Contains("tmpfs", StringComparison.OrdinalIgnoreCase));

        int w = report.Profiles.SelectMany(p => p.Options.WriterCounts).DefaultIfEmpty(16).Max();
        BenchRunResult? walDurable = durable?.Runs.FirstOrDefault(r => r.Engine == "SQLite WAL" && r.Writers == w);
        BenchRunResult? mwDurable = durable?.Runs.FirstOrDefault(r => r.Engine.Contains("multiwriter") && r.Writers == w);

        sb.AppendLine("## Why these numbers differ from the upstream multiwriter tables");
        sb.AppendLine();
        sb.AppendLine(
            "The [sqlite-multiwriter announcement](https://marcobambini.substack.com/p/we-solved-sqlites-single-writer-limitation) "
            + "reports ~8.6k tx/s for stock SQLite WAL and ~49k tx/s for multiwriter (16 threads, "
            + "`synchronous=FULL`, 100-row inserts) on an Apple M5 Pro. Absolute rates on durable Linux disk here "
            + "are far lower. That gap is mostly **fsync / storage**, not a different SQL workload — which is why "
            + "this article includes the tmpfs and `synchronous=OFF` profiles next to FULL-on-disk.");
        sb.AppendLine();
        sb.AppendLine(
            "- **Upstream’s harness** writes under `/tmp` on macOS (APFS on fast Apple Silicon NVMe). "
            + "FULL commits there are cheap enough that stock SQLite alone does multi‑thousand tx/s.");
        sb.AppendLine(
            "- **Linux `/tmp`** is often **tmpfs** (RAM). Our tmpfs profile lands in the same absolute ballpark "
            + "as their Mac SQLite baseline — and is **not** crash-safe storage.");
        sb.AppendLine(
            "- Multiwriter’s large **ratio** (≈5–6× vs SQLite) needs the single-writer **lock** to be the limiter "
            + "*and* enough sync bandwidth left for parallel publish. On slow FULL sync, everyone is "
            + "publish-bound and the ratio collapses.");
        sb.AppendLine();

        if (walDurable is not null && mwDurable is not null)
        {
            double gain = walDurable.WriteTxPerSecond <= 0
                ? 0
                : mwDurable.WriteTxPerSecond / walDurable.WriteTxPerSecond;
            sb.AppendLine(
                $"On our durable FULL profile at {w} writers: WAL **{walDurable.WriteTxPerSecond:F0}** vs "
                + $"multiwriter **{mwDurable.WriteTxPerSecond:F0}** (~{gain:F1}×). Flat scaling ⇒ fsync-bound. "
                + "On fast APFS / tmpfs, SQLite stalls on the write lock while multiwriter can still climb — "
                + "until *that* storage’s publish limit.");
            sb.AppendLine();
        }

        if (tmpfs is not null)
        {
            sb.AppendLine(
                $"See the **{tmpfs.ProfileTitle}** tables above for the inflated absolute rates when sync is free.");
            sb.AppendLine();
        }

        sb.AppendLine(
            "Other methodology differences vs their headline tables: we keep **concurrent readers** in the mix; "
            + "they measure writers-only in native C (`mw_bench`) with a short warmup. The insert shape "
            + "(own rows, 100 per tx, optional `mw_rebase=1`) is the same idea.");
        sb.AppendLine();
        sb.AppendLine(
            "So: compare **ratios and scaling shape** on one machine/FS, and always state durability. "
            + "Do not “fix” durability by parking the only copy of a DB on tmpfs.");
    }
}
