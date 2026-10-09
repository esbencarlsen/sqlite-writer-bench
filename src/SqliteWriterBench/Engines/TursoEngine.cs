using System.Data.Common;
using Turso;

namespace SqliteWriterBench.Engines;

/// <summary>Turso embedded engine with MVCC concurrent writers.</summary>
public sealed class TursoEngine : IDbEngine
{
    public string Name => "Turso MVCC";
    public string BeginWriteSql => "BEGIN CONCURRENT";

    public void EnsureReady() { }

    public DbConnection Open(string databasePath)
    {
        TursoConnection connection = new TursoConnection($"Data Source={databasePath};Pooling=False;Default Timeout=30");
        connection.Open();
        return connection;
    }

    public void Configure(DbConnection connection, string synchronous)
    {
        SqliteEngine.Exec(connection, "PRAGMA journal_mode='mvcc'");
        try
        {
            SqliteEngine.Exec(connection, "PRAGMA synchronous=" + SqliteEngine.NormalizeSync(synchronous));
        }
        catch
        {
            // Turso may ignore/reject some synchronous values.
        }

        try
        {
            SqliteEngine.Exec(connection, "PRAGMA busy_timeout=5000");
        }
        catch
        {
            // ignore
        }
    }

    public bool IsRetryable(Exception exception)
    {
        string message = exception.Message;
        return message.Contains("BUSY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Conflict", StringComparison.Ordinal)
            || message.Contains("no transaction is active", StringComparison.OrdinalIgnoreCase);
    }
}
