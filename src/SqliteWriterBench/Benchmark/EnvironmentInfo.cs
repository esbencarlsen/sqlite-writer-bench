using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SqliteWriterBench.Engines;
using Turso;

namespace SqliteWriterBench.Benchmark;

public static class EnvironmentInfo
{
    public static HardwareInfo CaptureHardware(string workDirectory)
    {
        string os = OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsWindows() ? "Windows"
            : "Unknown";

        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString(),
        };

        Directory.CreateDirectory(workDirectory);
        string fullWork = Path.GetFullPath(workDirectory);

        return new HardwareInfo
        {
            CpuModel = ReadCpuModel() ?? "unknown CPU",
            ProcessorCount = Environment.ProcessorCount,
            OsArch = $"{os} {arch}",
            WorkDirectory = fullWork,
            FileSystem = DetectFileSystem(fullWork),
        };
    }

    public static string DetectFileSystem(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(drive.DriveFormat))
            {
                return drive.DriveFormat;
            }
        }
        catch
        {
            // fall through
        }

        return ReadMountFileSystem(path) ?? "unknown";
    }

    private static string? ReadMountFileSystem(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/mounts"))
        {
            return null;
        }

        string full = Path.GetFullPath(path);
        string? bestMount = null;
        string? bestFs = null;
        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            string mount = parts[1].Replace("\\040", " ");
            string fs = parts[2];
            if (full == mount || full.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                if (bestMount is null || mount.Length > bestMount.Length)
                {
                    bestMount = mount;
                    bestFs = fs;
                }
            }
        }

        return bestFs;
    }

    public static LibraryVersions CaptureLibraryVersions()
    {
        string sqliteEngine = QuerySqlite("SELECT sqlite_version()");
        string multiwriter = ProbeMultiwriterVersion();
        string? tursoSql = ProbeTursoVersion();

        return new LibraryVersions
        {
            Sqlite = sqliteEngine,
            MicrosoftDataSqlite = GetAssemblyVersion(typeof(SqliteConnection).Assembly),
            TursoPackage = GetAssemblyVersion(typeof(TursoConnection).Assembly),
            TursoEngine = tursoSql,
            SqliteMultiwriter = multiwriter,
            DotNet = RuntimeInformation.FrameworkDescription,
        };
    }

    private static string ProbeMultiwriterVersion()
    {
        try
        {
            var engine = new MultiwriterEngine();
            engine.EnsureReady();
            return MultiwriterEngine.LoadedVersion ?? QueryMultiwriterVersion();
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static string QueryMultiwriterVersion()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        string path = MultiwriterEngine.ResolveExtensionPath();
        conn.LoadExtension(path, "sqlite3_multiwriter_init");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT mw_version()";
        return cmd.ExecuteScalar()?.ToString() ?? "unknown";
    }

    private static string? ProbeTursoVersion()
    {
        try
        {
            using var conn = new TursoConnection("Data Source=:memory:;Pooling=False");
            conn.Open();
            foreach (var sql in new[] { "SELECT turso_version()", "SELECT sqlite_version()" })
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    var value = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
                catch
                {
                    // try next probe
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string QuerySqlite(string sql)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "unknown";
    }

    private static string GetAssemblyVersion(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip build metadata (+sha) for cleaner article output.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string? ReadCpuModel()
    {
        const string path = "/proc/cpuinfo";
        if (!File.Exists(path))
        {
            return null;
        }

        string? model = null;
        string? hardware = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
            {
                model = line.Split(':', 2)[1].Trim();
                break;
            }

            if (line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
            {
                hardware = line.Split(':', 2)[1].Trim();
            }
        }

        return model ?? hardware;
    }
}
