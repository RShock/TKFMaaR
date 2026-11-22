using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class EmulatorConnectionDefaults
{
    // Keep the original device selected when MFA executes the resulting command.
    internal static string[] Resolve(string adb, string serial, string deviceConfig,
        bool autoDiscover, string customPath, string customArguments)
    {
        if (!File.Exists(adb) || string.IsNullOrWhiteSpace(serial)) return Array.Empty<string>();

        const string manager = "MuMuManager.exe";
        string manual = customPath.Trim().Trim('"');
        bool windows = OperatingSystem.IsWindows();
        bool muMuManager = windows && string.Equals(Path.GetFileName(manual), manager, StringComparison.OrdinalIgnoreCase);
        bool muMuDirectory = windows && Directory.Exists(manual);
        if (manual.Length > 0 && !muMuManager && !muMuDirectory)
            return Path.IsPathFullyQualified(manual) && File.Exists(manual)
                ? new[] { manual, customArguments.Trim() } : Array.Empty<string>();
        if (muMuManager && !string.IsNullOrWhiteSpace(customArguments))
            return Path.IsPathFullyQualified(manual) && File.Exists(manual)
                ? new[] { manual, customArguments.Trim() } : Array.Empty<string>();
        if (!windows || (manual.Length == 0 && !autoDiscover)
            || !Uri.TryCreate("tcp://" + serial, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Port <= 0)
            return Array.Empty<string>();

        JsonNode? mumu;
        try { mumu = JsonNode.Parse(deviceConfig)?["extras"]?["mumu"]; }
        catch (JsonException) { return Array.Empty<string>(); }
        if (!int.TryParse(mumu?["index"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            return Array.Empty<string>();

        var candidates = new List<string>();
        if (muMuDirectory)
        {
            candidates.Add(Path.Combine(manual, manager));
            candidates.Add(Path.Combine(manual, "nx_main", manager));
        }
        else if (muMuManager) candidates.Add(manual);
        else
        {
            if (Path.GetDirectoryName(adb) is string adbDirectory) candidates.Add(Path.Combine(adbDirectory, manager));
            string? root = mumu?["path"]?.ToString();
            if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root, "nx_main", manager));
        }

        string? path = candidates.FirstOrDefault(File.Exists);
        return path == null ? Array.Empty<string>() : new[] { Path.GetFullPath(path), $"control -v {index.ToString(CultureInfo.InvariantCulture)} launch" };
    }
}
