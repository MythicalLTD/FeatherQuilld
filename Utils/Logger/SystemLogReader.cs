using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.Logger;

/// <summary>Reads FeatherQuilld daemon log files from the configured logs directory.</summary>
public static partial class SystemLogReader
{
    private const int MaxLines = 5000;

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeFileNameRegex();

    public static IReadOnlyList<SystemLogFileEntry> ListFiles(string logsDirectory)
    {
        var dir = Path.GetFullPath(logsDirectory);
        if (!Directory.Exists(dir))
            return [];

        var entries = new List<SystemLogFileEntry>();

        var latest = Path.Combine(dir, "latest.log");
        if (File.Exists(latest))
        {
            var info = new FileInfo(latest);
            entries.Add(new SystemLogFileEntry(
                Name: "latest.log",
                SizeBytes: info.Length,
                ModifiedAt: info.LastWriteTimeUtc,
                Compressed: false));
        }

        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.log.gz").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            entries.Add(new SystemLogFileEntry(
                Name: file.Name,
                SizeBytes: file.Length,
                ModifiedAt: file.LastWriteTimeUtc,
                Compressed: true));
        }

        return entries;
    }

    public static string ReadTail(string logsDirectory, string fileName, int lines)
    {
        if (!SafeFileNameRegex().IsMatch(fileName))
            throw new InvalidOperationException("Invalid log file name.");

        var dir = Path.GetFullPath(logsDirectory);
        var path = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != dir)
            throw new InvalidOperationException("Invalid log file path.");

        if (!File.Exists(path))
            throw new FileNotFoundException("Log file not found.", fileName);

        lines = Math.Clamp(lines, 1, MaxLines);

        return fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? TailGzip(path, lines)
            : TailText(path, lines);
    }

    private static string TailText(string path, int lines)
    {
        var buffer = new Queue<string>(lines);
        foreach (var line in File.ReadLines(path))
        {
            if (buffer.Count == lines)
                buffer.Dequeue();
            buffer.Enqueue(line);
        }

        return string.Join('\n', buffer);
    }

    private static string TailGzip(string path, int lines)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var buffer = new Queue<string>(lines);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (buffer.Count == lines)
                buffer.Dequeue();
            buffer.Enqueue(line);
        }

        return string.Join('\n', buffer);
    }
}

public sealed record SystemLogFileEntry(
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    bool Compressed);
