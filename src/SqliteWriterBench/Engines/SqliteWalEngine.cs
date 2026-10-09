using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace SqliteWriterBench.Engines;

/// <summary>Stock SQLite with WAL journal mode.</summary>
public sealed class SqliteWalEngine : IDbEngine
{
    public string Name => "SQLite WAL";
    public string BeginWriteSql => "BEGIN IMMEDIATE";

    public void EnsureReady() { }

    public DbConnection Open(string databasePath)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Configure(DbConnection connection, string synchronous)
    {
        SqliteEngine.Exec(connection, "PRAGMA journal_mode=WAL");
        SqliteEngine.Exec(connection, "PRAGMA synchronous=" + SqliteEngine.NormalizeSync(synchronous));
        SqliteEngine.Exec(connection, "PRAGMA busy_timeout=5000");
        SqliteEngine.Exec(connection, "PRAGMA temp_store=MEMORY");
    }

    public bool IsRetryable(Exception exception) => SqliteEngine.IsSqliteRetryable(exception);
}
