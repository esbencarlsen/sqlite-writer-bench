using System.Data.Common;

namespace SqliteWriterBench.Engines;

public interface IDbEngine
{
    string Name { get; }

    /// <summary>SQL used to begin a write transaction (e.g. BEGIN or BEGIN CONCURRENT).</summary>
    string BeginWriteSql { get; }

    /// <summary>One-time process setup (e.g. load a VFS extension).</summary>
    void EnsureReady();

    DbConnection Open(string databasePath);

    /// <param name="synchronous">SQLite synchronous pragma: FULL, NORMAL, or OFF.</param>
    void Configure(DbConnection connection, string synchronous);

    bool IsRetryable(Exception exception);
}
