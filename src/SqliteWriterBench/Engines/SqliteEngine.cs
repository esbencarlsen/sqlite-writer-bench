using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace SqliteWriterBench.Engines;

/// <summary>Stock SQLite with DELETE (rollback) journal mode.</summary>
public sealed class SqliteEngine : IDbEngine
{
    public string Name => "SQLite";
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
        Exec(connection, "PRAGMA journal_mode=DELETE");
        Exec(connection, "PRAGMA synchronous=" + NormalizeSync(synchronous));
        Exec(connection, "PRAGMA busy_timeout=5000");
        Exec(connection, "PRAGMA temp_store=MEMORY");
    }

    public bool IsRetryable(Exception exception) => IsSqliteRetryable(exception);

    internal static string NormalizeSync(string synchronous) =>
        synchronous.Trim().ToUpperInvariant() switch
        {
            "OFF" or "0" => "OFF",
            "NORMAL" or "1" => "NORMAL",
            _ => "FULL",
        };

    internal static bool IsSqliteRetryable(Exception exception)
    {
        if (exception is not SqliteException se)
        {
            return false;
        }

        if (se.SqliteErrorCode is 5 or 6 || se.SqliteExtendedErrorCode is 5 or 6 or 517)
        {
            return true;
        }

        return se.Message.Contains("no transaction is active", StringComparison.OrdinalIgnoreCase)
            || se.Message.Contains("cannot commit", StringComparison.OrdinalIgnoreCase)
            || se.Message.Contains("cannot rollback", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Exec(DbConnection connection, string sql)
    {
        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
