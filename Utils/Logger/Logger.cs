using System.IO.Compression;
using System.Text;
using FeatherQuilld.Utils;

namespace FeatherQuilld.Utils.Logger;

/// <summary>
/// Minecraft-style logger: colored console + <c>logs/latest.log</c>,
/// rotating previous sessions into gzipped dated archives on each start.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly bool _debug;
    private readonly string _latestPath;
    private bool _disposed;

    public string LogsDirectory { get; }

    public Logger(LoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        LogsDirectory = Path.GetFullPath(options.Directory);
        _debug = options.Debug;
        Directory.CreateDirectory(LogsDirectory);

        _latestPath = Path.Combine(LogsDirectory, "latest.log");
        RotateOnStart(_latestPath, Math.Max(0, options.MaxArchives));

        _writer = new StreamWriter(_latestPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void Debug(LoggerTypes type, string message) =>
        Log(type, LoggerLevel.Debug, message);

    public void Info(LoggerTypes type, string message) =>
        Log(type, LoggerLevel.Info, message);

    public void Warning(LoggerTypes type, string message) =>
        Log(type, LoggerLevel.Warning, message);

    public void Error(LoggerTypes type, string message) =>
        Log(type, LoggerLevel.Error, message);

    public void Error(LoggerTypes type, string message, Exception exception) =>
        Log(type, LoggerLevel.Error, $"{message}: {exception}");

    public void Log(LoggerTypes type, LoggerLevel level, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (level == LoggerLevel.Debug && !_debug)
            return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var levelName = level.ToString().ToUpperInvariant();
        var plain = $"[{ts}] [{levelName}] [{type}] {ColoredConsole.StripCodes(message)}";
        var colored =
            $"&7[{ts}] {LevelColor(level)}[{levelName}]&r &b[{type}]&r &f{message}&r";

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(plain);
            ColoredConsole.WriteLine(colored);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _writer.Dispose();
        }
    }

    private static string LevelColor(LoggerLevel level) => level switch
    {
        LoggerLevel.Debug => "&8",
        LoggerLevel.Info => "&a",
        LoggerLevel.Warning => "&e",
        LoggerLevel.Error => "&c",
        _ => "&7"
    };

    private static void RotateOnStart(string latestPath, int maxArchives)
    {
        if (!File.Exists(latestPath))
            return;

        var day = File.GetLastWriteTimeUtc(latestPath).ToString("yyyy-MM-dd");
        var dir = Path.GetDirectoryName(latestPath)!;

        var n = 1;
        string archive;
        do
        {
            archive = Path.Combine(dir, $"{day}-{n}.log.gz");
            n++;
        } while (File.Exists(archive));

        CompressAndDelete(latestPath, archive);
        var dirCopy = dir;
        var maxCopy = maxArchives;
        _ = Task.Run(() => PruneOldArchives(dirCopy, maxCopy));
    }

    private const long FastRotateMaxBytes = 10 * 1024 * 1024;

    private static void CompressAndDelete(string sourcePath, string archivePath)
    {
        var size = new FileInfo(sourcePath).Length;
        if (size > FastRotateMaxBytes)
        {
            File.Move(sourcePath, archivePath.Replace(".gz", "", StringComparison.Ordinal), overwrite: true);
            return;
        }

        using (var input = File.OpenRead(sourcePath))
        using (var output = File.Create(archivePath))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            input.CopyTo(gzip);
        }

        File.Delete(sourcePath);
    }

    private static void PruneOldArchives(string dir, int maxArchives)
    {
        if (maxArchives <= 0)
            return;

        var excess = new DirectoryInfo(dir)
            .EnumerateFiles("*.log.gz")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(maxArchives);

        foreach (var file in excess)
            file.Delete();
    }
}
