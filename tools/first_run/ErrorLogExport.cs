using System.Diagnostics;
using System.IO.Compression;

internal static class ErrorLogExport
{
    internal static string Create(string logsDirectory, string installRoot)
    {
        var cutoff = DateTime.UtcNow.AddDays(-3);
        var debug = Path.Combine(installRoot, "debug");
        var errors = Path.Combine(debug, "on_error");
        var output = Path.Combine(logsDirectory, "exports");
        string name = $"TKFMaaR-error-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip";
        string archivePath = Path.Combine(output, name);
        string pending = archivePath + ".tmp";
        var entries = Collect(logsDirectory, "logs", IsLog, cutoff)
            .Concat(Collect(debug, "debug", IsLog, cutoff))
            .Concat(Collect(errors, "debug/on_error", IsImage, cutoff))
            .ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("最近三天没有可导出的日志或故障截图。");

        Directory.CreateDirectory(output);
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    using var source = new FileStream(entry.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var target = archive.CreateEntry(entry.Name, CompressionLevel.Fastest).Open();
                    // Live logs can grow during export. Copy the full file as of opening it.
                    CopySnapshot(source, target);
                }
            }
            File.Move(pending, archivePath);
            return archivePath;
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    private static IEnumerable<(string Path, string Name)> Collect(string directory, string prefix,
        Func<string, bool> matches, DateTime cutoff)
    {
        if (!Directory.Exists(directory)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(directory, "*", options))
        {
            if (!matches(Path.GetFileName(path)) || File.GetLastWriteTimeUtc(path) < cutoff) continue;
            yield return (path, prefix + "/" + Path.GetRelativePath(directory, path).Replace('\\', '/'));
        }
    }

    private static bool IsLog(string name) => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".log.", StringComparison.OrdinalIgnoreCase);

    private static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    private static void CopySnapshot(FileStream source, Stream target)
    {
        long remaining = source.Length;
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new IOException("导出期间日志已被截断，请重新导出。");
            target.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    internal static void Reveal(string archivePath)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            start.Arguments = "/select,\"" + archivePath + "\"";
        }
        else if (OperatingSystem.IsMacOS())
        {
            start.FileName = "open";
            start.ArgumentList.Add("-R");
            start.ArgumentList.Add(archivePath);
        }
        else
        {
            start.FileName = "xdg-open";
            start.ArgumentList.Add(Path.GetDirectoryName(archivePath)!);
        }
        using var process = Process.Start(start) ?? throw new IOException("无法打开日志文件夹。");
    }
}
