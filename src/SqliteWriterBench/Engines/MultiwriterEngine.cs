using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace SqliteWriterBench.Engines;

/// <summary>Stock SQLite plus sqlite-multiwriter VFS extension.</summary>
public sealed class MultiwriterEngine : IDbEngine
{
    private static readonly Lock Gate = new();
    private static bool _loaded;

    public static string? LoadedVersion { get; private set; }

    public string Name => "sqlite-multiwriter";
    public string BeginWriteSql => "BEGIN";

    public void EnsureReady()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            string extensionPath = ResolveExtensionPath();
            using SqliteConnection bootstrap = new SqliteConnection("Data Source=:memory:");
            bootstrap.Open();
            bootstrap.LoadExtension(extensionPath, "sqlite3_multiwriter_init");
            using SqliteCommand version = bootstrap.CreateCommand();
            version.CommandText = "SELECT mw_version()";
            string ver = version.ExecuteScalar()?.ToString() ?? "?";
            LoadedVersion = ver;
            Console.Error.WriteLine($"Loaded sqlite-multiwriter {ver} from {extensionPath}");
            _loaded = true;
        }
    }

    public DbConnection Open(string databasePath)
    {
        EnsureReady();
        string absolute = Path.GetFullPath(databasePath);
        // URI required so vfs= / mw_* parameters are honored by multiwriter.
        string uri = $"file:{absolute}?vfs=multiwriter&mw_rebase=1";
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Configure(DbConnection connection, string synchronous)
    {
        // multiwriter requires WAL.
        SqliteEngine.Exec(connection, "PRAGMA journal_mode=WAL");
        SqliteEngine.Exec(connection, "PRAGMA synchronous=" + SqliteEngine.NormalizeSync(synchronous));
        SqliteEngine.Exec(connection, "PRAGMA busy_timeout=5000");
        SqliteEngine.Exec(connection, "PRAGMA temp_store=MEMORY");
    }

    public bool IsRetryable(Exception exception) => SqliteEngine.IsSqliteRetryable(exception);

    public static string ResolveExtensionPath()
    {
        List<string> candidates = [];

        string? env = Environment.GetEnvironmentVariable("MULTIWRITER_EXT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        string? repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            candidates.Add(Path.Combine(repoRoot, "native", "multiwriter.so"));
            candidates.Add(Path.Combine(repoRoot, "native", "libmultiwriter.so"));
            candidates.Add(Path.Combine(repoRoot, "native", "multiwriter.dylib"));
            candidates.Add(Path.Combine(repoRoot, "native", "multiwriter.dll"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "multiwriter.so"));

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        throw new FileNotFoundException(
            "sqlite-multiwriter extension not found. Run scripts/fetch-multiwriter.sh "
            + "or set MULTIWRITER_EXT to the path of multiwriter.so.");
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "fetch-multiwriter.sh"))
                || Directory.Exists(Path.Combine(dir.FullName, "native")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Walk up from cwd as a fallback when running via `dotnet run`.
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
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
}