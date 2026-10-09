using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using SqliteWriterBench.Engines;

namespace SqliteWriterBench.Benchmark;

public sealed class BenchRunner(BenchOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<BenchSuiteResult> RunAllAsync(IEnumerable<IDbEngine> engines, CancellationToken ct)
    {
        // Prefer on-disk project tmp/ over /tmp (tmpfs). synchronous=FULL on tmpfs
        // looks like thousands of tx/s and is not durable storage.
        string workBase = options.WorkDirectory ?? FindWorkRootBase();
        string workRoot = Path.Combine(workBase, "sqlite-writer-bench-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        Console.Error.WriteLine(
            $"Profile: {options.ProfileId} | sync={options.Synchronous} | work={workRoot} (fs={EnvironmentInfo.DetectFileSystem(workRoot)})");

        List<BenchRunResult> runs = [];
        try
        {
            foreach (IDbEngine engine in engines)
            {
                if (options.EnginesFilter is not null
                    && !engine.Name.Contains(options.EnginesFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                engine.EnsureReady();
                foreach (int writers in options.WriterCounts)
                {
                    ct.ThrowIfCancellationRequested();
                    Console.Error.WriteLine($"=== {engine.Name} | writers={writers} readers={options.ReaderCount}");
                    try
                    {
                        BenchRunResult result = await RunOneAsync(engine, writers, workRoot, ct);
                        runs.Add(result);
                        Console.Error.WriteLine(
                            $"    write {result.WriteTxPerSecond:F0} tx/s | read {result.ReadOpsPerSecond:F0} ops/s | "
                            + $"p99.9 {result.WriteLatencyMs.P999:F2} ms | retries/100tx {result.RetriesPer100Tx:F1}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"    FAILED: {ex.Message}");
                        runs.Add(FailedRun(engine.Name, writers, options.ReaderCount, options.Duration.TotalSeconds, ex));
                    }

                    // Give SQLite a moment to release file handles between cells.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(250, ct);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }

        string sync = SqliteEngine.NormalizeSync(options.Synchronous);
        return new BenchSuiteResult
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProfileId = options.ProfileId,
            ProfileTitle = options.ProfileTitle,
            ProfileSummary = options.ProfileSummary,
            Hardware = EnvironmentInfo.CaptureHardware(workBase),
            Versions = EnvironmentInfo.CaptureLibraryVersions(),
            Options = new BenchOptionsSnapshot
            {
                DurationSeconds = options.Duration.TotalSeconds,
                WriterCounts = options.WriterCounts,
                ReaderCount = options.ReaderCount,
                RowsPerTransaction = options.RowsPerTransaction,
                PayloadBytes = options.PayloadBytes,
                Durability = $"synchronous={sync}",
                Notes = "Each writer inserts its own rows (low true conflicts). Readers use indexed point/range lookups (not full-table COUNT(*)).",
            },
            Runs = runs,
        };
    }

    public static async Task WriteReportAsync(BenchReport report, string resultsDir)
    {
        Directory.CreateDirectory(resultsDir);
        string stamp = report.Timestamp.ToString("yyyyMMdd-HHmmss");
        string stamped = Path.Combine(resultsDir, $"bench-{stamp}.json");
        string latest = Path.Combine(resultsDir, "latest.json");
        string json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(stamped, json);
        await File.WriteAllTextAsync(latest, json);
        Console.Error.WriteLine($"Wrote {latest}");
        Console.Error.WriteLine($"Wrote {stamped}");
    }

    public static async Task WriteResultsAsync(BenchSuiteResult suite, string resultsDir)
    {
        await WriteReportAsync(
            new BenchReport
            {
                Timestamp = suite.Timestamp,
                Versions = suite.Versions,
                Profiles = [suite],
            },
            resultsDir);
    }

    public static async Task<BenchReport> LoadLatestReportAsync(string resultsDir)
    {
        string path = Path.Combine(resultsDir, "latest.json");
        string text = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("profiles", out _))
        {
            return JsonSerializer.Deserialize<BenchReport>(text, JsonOptions)
                   ?? throw new InvalidOperationException($"Failed to parse {path}");
        }

        // Legacy single-suite JSON (pre multi-profile).
        BenchSuiteResult suite = JsonSerializer.Deserialize<BenchSuiteResult>(text, JsonOptions)
                                 ?? throw new InvalidOperationException($"Failed to parse {path}");
        return new BenchReport
        {
            Timestamp = suite.Timestamp,
            Versions = suite.Versions,
            Profiles = [suite],
        };
    }

    private async Task<BenchRunResult> RunOneAsync(IDbEngine engine, int writers, string workRoot, CancellationToken ct)
    {
        string dbDir = Path.Combine(workRoot, Sanitize(engine.Name), $"w{writers}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbDir);
        string dbPath = Path.Combine(dbDir, "bench.db");

        try
        {
            return await RunOneCoreAsync(engine, writers, dbPath, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(dbDir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private async Task<BenchRunResult> RunOneCoreAsync(IDbEngine engine, int writers, string dbPath, CancellationToken ct)
    {
        OpenConfigured(engine, dbPath);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Duration);

        byte[] payload = new byte[options.PayloadBytes];
        Random.Shared.NextBytes(payload);

        ConcurrentBag<double> writeLatencies = [];
        long writeTx = 0, writeRetries = 0, writeErrors = 0, readOps = 0, readErrors = 0;

        Stopwatch sw = Stopwatch.StartNew();
        List<Task> tasks = [];

        for (int i = 0; i < writers; i++)
        {
            int writerId = i;
            tasks.Add(Task.Run(() =>
            {
                using DbConnection conn = engine.Open(dbPath);
                engine.Configure(conn, options.Synchronous);
                long localSeq = 0L;
                while (!cts.IsCancellationRequested)
                {
                    long started = Stopwatch.GetTimestamp();
                    int retries = 0;
                    bool ok = false;
                    Exception? last = null;

                    for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
                    {
                        try
                        {
                            // Clear any leftover txn state from a prior conflict.
                            TryRollback(conn);
                            Exec(conn, engine.BeginWriteSql);
                            using (DbCommand insert = conn.CreateCommand())
                            {
                                insert.CommandText =
                                    "INSERT INTO events(writer_id, seq, payload, created_at) VALUES ($w, $s, $p, $t)";
                                DbParameter pW = insert.CreateParameter();
                                pW.ParameterName = "$w";
                                insert.Parameters.Add(pW);
                                DbParameter pS = insert.CreateParameter();
                                pS.ParameterName = "$s";
                                insert.Parameters.Add(pS);
                                DbParameter pP = insert.CreateParameter();
                                pP.ParameterName = "$p";
                                pP.Value = payload;
                                insert.Parameters.Add(pP);
                                DbParameter pT = insert.CreateParameter();
                                pT.ParameterName = "$t";
                                insert.Parameters.Add(pT);

                                string createdAt = DateTimeOffset.UtcNow.ToString("O");
                                for (int r = 0; r < options.RowsPerTransaction; r++)
                                {
                                    pW.Value = writerId;
                                    pS.Value = localSeq++;
                                    pT.Value = createdAt;
                                    insert.ExecuteNonQuery();
                                }
                            }

                            Exec(conn, "COMMIT");
                            ok = true;
                            break;
                        }
                        catch (Exception ex) when (engine.IsRetryable(ex))
                        {
                            last = ex;
                            retries++;
                            TryRollback(conn);
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                            TryRollback(conn);
                            break;
                        }
                    }

                    double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (ok)
                    {
                        Interlocked.Increment(ref writeTx);
                        Interlocked.Add(ref writeRetries, retries);
                        writeLatencies.Add(elapsedMs);
                    }
                    else
                    {
                        Interlocked.Increment(ref writeErrors);
                        Interlocked.Add(ref writeRetries, retries);
                        if (last is not null && writeErrors <= 3)
                        {
                            Console.Error.WriteLine($"  write error ({engine.Name}): {last.Message}");
                        }
                    }
                }
            }, CancellationToken.None));
        }

        for (int i = 0; i < options.ReaderCount; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                using DbConnection conn = engine.Open(dbPath);
                engine.Configure(conn, options.Synchronous);
                int flip = 0;
                // Avoid full-table COUNT(*) — as the DB grows it dominates the run and
                // makes durable engines look artificially write-starved on long durations.
                using DbCommand maxCmd = conn.CreateCommand();
                maxCmd.CommandText = "SELECT MAX(id) FROM events";
                using DbCommand rangeCmd = conn.CreateCommand();
                rangeCmd.CommandText =
                    "SELECT seq, length(payload) FROM events WHERE writer_id = $w ORDER BY seq DESC LIMIT 16";
                DbParameter rangeWriter = rangeCmd.CreateParameter();
                rangeWriter.ParameterName = "$w";
                rangeCmd.Parameters.Add(rangeWriter);

                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        if ((flip++ & 1) == 0)
                        {
                            _ = maxCmd.ExecuteScalar();
                        }
                        else
                        {
                            rangeWriter.Value = flip % Math.Max(writers, 1);
                            using DbDataReader reader = rangeCmd.ExecuteReader();
                            while (reader.Read())
                            {
                                // drain
                            }
                        }

                        Interlocked.Increment(ref readOps);
                        // Pace readers so ultra-cheap lookups cannot busy-spin all cores
                        // and starve durable writers under synchronous=FULL.
                        Thread.Sleep(1);
                    }
                    catch (Exception ex) when (engine.IsRetryable(ex))
                    {
                        // Contended reads under DELETE journal / engine warmup — don't count as hard errors.
                        Thread.Sleep(1);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref readErrors);
                        if (readErrors <= 3)
                        {
                            Console.Error.WriteLine($"  read error ({engine.Name}): {ex.Message}");
                        }

                        Thread.Sleep(1);
                    }
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // expected when duration elapses
        }

        sw.Stop();
        double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        List<double> latencyList = [.. writeLatencies];
        latencyList.Sort();

        long tx = Interlocked.Read(ref writeTx);
        long retries = Interlocked.Read(ref writeRetries);

        return new BenchRunResult
        {
            Engine = engine.Name,
            Writers = writers,
            Readers = options.ReaderCount,
            DurationSeconds = seconds,
            WriteTransactions = tx,
            WriteRetries = retries,
            WriteErrors = Interlocked.Read(ref writeErrors),
            ReadOperations = Interlocked.Read(ref readOps),
            ReadErrors = Interlocked.Read(ref readErrors),
            WriteTxPerSecond = tx / seconds,
            ReadOpsPerSecond = Interlocked.Read(ref readOps) / seconds,
            RetriesPer100Tx = tx == 0 ? 0 : retries * 100.0 / tx,
            WriteLatencyMs = LatencyStats.FromSorted(latencyList),
        };
    }

    private void OpenConfigured(IDbEngine engine, string dbPath)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using DbConnection setup = engine.Open(dbPath);
                engine.Configure(setup, options.Synchronous);
                CreateSchema(setup);
                return;
            }
            catch (Exception ex) when (IsTransientIo(ex))
            {
                last = ex;
                Thread.Sleep(100 * (attempt + 1));
            }
        }

        throw last ?? new InvalidOperationException("Failed to open/configure database.");
    }

    private static void CreateSchema(DbConnection connection)
    {
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS events (
              id INTEGER PRIMARY KEY,
              writer_id INTEGER NOT NULL,
              seq INTEGER NOT NULL,
              payload BLOB NOT NULL,
              created_at TEXT NOT NULL
            );
            """);
        Exec(connection, "CREATE INDEX IF NOT EXISTS idx_events_writer ON events(writer_id);");
    }

    private static void Exec(DbConnection connection, string sql)
    {
        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryRollback(DbConnection connection)
    {
        try
        {
            Exec(connection, "ROLLBACK");
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsTransientIo(Exception ex) =>
        ex.Message.Contains("disk I/O", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("unable to open", StringComparison.OrdinalIgnoreCase);

    private static BenchRunResult FailedRun(string engine, int writers, int readers, double durationSeconds, Exception _) =>
        new()
        {
            Engine = engine,
            Writers = writers,
            Readers = readers,
            DurationSeconds = durationSeconds,
            WriteTransactions = 0,
            WriteRetries = 0,
            WriteErrors = 1,
            ReadOperations = 0,
            ReadErrors = 0,
            WriteTxPerSecond = 0,
            ReadOpsPerSecond = 0,
            RetriesPer100Tx = 0,
            WriteLatencyMs = LatencyStats.FromSorted([]),
        };

    private static string FindWorkRootBase()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "fetch-multiwriter.sh")))
            {
                string tmp = Path.Combine(dir.FullName, "tmp");
                Directory.CreateDirectory(tmp);
                return tmp;
            }

            dir = dir.Parent;
        }

        return Path.GetTempPath();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
}
