using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// User files must live outside the native updater's resource/ and agent/ cleanup.
// Run before MFA reads settings, connects a device or checks for updates.
internal static class UserDataMigration
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new()
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject ReadObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
        ?? throw new IOException("配置必须为 JSON 对象：" + path);

    private static void WriteObject(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, value.ToJsonString(WriteOptions) + "\n");
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void PlainPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("自动迁移不跟随符号链接或目录联接：" + path);
    }

    private static IEnumerable<string> Files(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        PlainPath(folder);
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
            PlainPath(path);
            if (Directory.Exists(path))
                foreach (string child in Files(path)) yield return child;
            else yield return path;
        }
    }

    private static bool SameFile(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
    }

    private static void CopyTree(string source, string target)
    {
        foreach (string file in Files(source))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            if (!SameFile(file, destination)) throw new IOException("备份不完整：" + file);
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
                foreach (string childText in Strings(pair.Value)) yield return childText;
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                foreach (string childText in Strings(item)) yield return childText;
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            yield return text;
            JsonNode? nested = null;
            if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                try { nested = JsonNode.Parse(text, documentOptions: ReadOptions); }
                catch (JsonException) { }
            if (nested != null)
                foreach (string child in Strings(nested)) yield return child;
        }
    }

    private static void MigrateSchedule(JsonObject instance, JsonArray tasks, JsonObject launch)
    {
        const string marker = "TkfmScheduleOptionsV1";
        if (instance[marker]?.GetValue<bool>() == true) return;
        var options = launch["option"] as JsonArray ?? new JsonArray();
        if (launch["option"] == null) launch["option"] = options;
        static JsonObject? Find(JsonArray? options, string name) => options?.OfType<JsonObject>()
            .FirstOrDefault(option => option["name"]?.ToString() == name);
        static bool Enabled(JsonObject? option) => option?["index"]?.ToString() == "0";
        static void Remove(JsonArray? options, params string[] names)
        {
            if (options == null) return;
            for (int i = options.Count - 1; i >= 0; i--)
                if (names.Contains(options[i]?["name"]?.ToString())) options.RemoveAt(i);
        }
        var dispatchTask = tasks.OfType<JsonObject>().FirstOrDefault(task => task["entry"]?.ToString() == "DailyDispatchStart");
        var dispatchOptions = dispatchTask?["option"] as JsonArray;
        var oldDispatch = Find(dispatchOptions, "定时派遣");
        var dispatchSub = oldDispatch?["sub_options"] as JsonArray;
        var oldEmulator = Find(dispatchSub, "定时启停MuMu模拟器");
        if (oldDispatch != null && Find(options, "定时派遣") == null)
        {
            var moved = (JsonObject)oldDispatch.DeepClone();
            Remove(moved["sub_options"] as JsonArray, "定时启停MuMu模拟器", "派遣成功后关闭游戏");
            options.Add(moved);
        }
        void AddEnabled(string name)
        {
            if (Find(options, name) == null) options.Add(new JsonObject { ["name"] = name, ["index"] = 0 });
        }
        if (Enabled(oldEmulator)) AddEnabled("定时任务允许自动启动模拟器");
        if (Enabled(oldEmulator) && Enabled(Find(oldEmulator?["sub_options"] as JsonArray, "派遣成功后关闭MuMu模拟器")))
            AddEnabled("定时任务执行完毕后关闭模拟器");
        var manual = Find(options, "MuMu管理程序路径")?["data"];
        if (manual?["路径"]?.ToString() is string path && !string.IsNullOrWhiteSpace(path))
        {
            instance["TkfmSavedEmulatorPath"] = path;
            instance["TkfmSavedEmulatorArguments"] = manual["参数"]?.ToString() ?? "";
            instance["TkfmSavedEmulatorSerial"] = instance["AdbDevice"]?["AdbSerial"]?.ToString() ?? "";
        }
        if (instance["TkfmMuMuConnectionPath"] is JsonNode ownedPath && JsonNode.DeepEquals(instance["SoftwarePath"], ownedPath)
            && JsonNode.DeepEquals(instance["EmulatorConfig"], instance["TkfmMuMuConnectionArguments"]))
        {
            instance.Remove("SoftwarePath");
            instance.Remove("EmulatorConfig");
        }
        instance.Remove("TkfmMuMuConnectionPath");
        instance.Remove("TkfmMuMuConnectionArguments");
        foreach (var task in tasks.OfType<JsonObject>())
        {
            switch (task["entry"]?.ToString())
            {
                case "GameLoginStart":
                    Remove(task["option"] as JsonArray, "启动MuMu模拟器", "自动寻找MuMu", "MuMu管理程序路径", "MuMu实例编号");
                    (task["pipeline_override"] as JsonObject)?.Remove("GameLoginStartOptions");
                    break;
                case "DailyDispatchStart":
                    Remove(task["option"] as JsonArray, "定时派遣", "自动根据倒计时再后续执行派遣", "派遣成功后关闭游戏");
                    (task["pipeline_override"] as JsonObject)?.Remove("DailyDispatchScheduleConfig");
                    break;
            }
        }
        instance[marker] = true;
    }

    private static void MigrateFastDispatch(JsonObject instance, JsonObject launch)
    {
        const string marker = "TkfmFastDispatchV1";
        if (instance[marker]?.GetValue<bool>() == true) return;
        if (launch["option"] is JsonArray options)
        {
            var old = options.OfType<JsonObject>().FirstOrDefault(option => option["name"]?.ToString() == "定时派遣");
            if (old != null)
            {
                if (!options.OfType<JsonObject>().Any(option => option["name"]?.ToString() == "自动最速派遣"))
                {
                    old["name"] = "自动最速派遣";
                    old.Remove("sub_options");
                }
                else options.Remove(old);
            }
        }
        if (instance["TkfmDispatchReservation"]?.ToString() is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonObject reservation && reservation["state"]?.ToString() == "pending"
                    && reservation["from_countdown"]?.GetValue<bool>() != true)
                    instance.Remove("TkfmDispatchReservation");
            }
            catch (JsonException) { /* Preserve unreadable legacy data for recovery. */ }
        }
        instance.Remove("TkfmLastDispatchMinute");
        instance[marker] = true;
    }

    private static void MoveFastDispatchToTask(JsonArray tasks, JsonObject launch)
    {
        const string optionName = "自动最速派遣";
        if (launch["option"] is not JsonArray launchOptions) return;
        var old = launchOptions.OfType<JsonObject>().FirstOrDefault(option => option["name"]?.ToString() == optionName);
        if (old == null) return;
        var dispatch = tasks.OfType<JsonObject>().FirstOrDefault(task => task["entry"]?.ToString() == "DailyDispatchStart");
        if (dispatch == null) return;
        if (dispatch["option"] is not JsonArray options)
        {
            options = new JsonArray();
            dispatch["option"] = options;
        }
        if (!options.OfType<JsonObject>().Any(option => option["name"]?.ToString() == optionName))
            options.Add(old.DeepClone());
        launchOptions.Remove(old);
    }

    private static bool ApplyDesktopUiPolicy(JsonObject config)
    {
        bool changed = config.Remove("GitHubToken");
        if (config["UI.HasCompletedFirstUseTutorial"] is not JsonValue completed ||
            !completed.TryGetValue<bool>(out bool alreadySkipped) || !alreadySkipped)
        {
            config["UI.HasCompletedFirstUseTutorial"] = true;
            changed = true;
        }
        if (OperatingSystem.IsWindows())
        {
            changed |= config.Remove("ShouldMinimizeToTray");
            changed |= config.Remove("TkfmCloseToTray");
        }
        return changed;
    }

    internal static void Apply(string root)
    {
        var definition = ReadObject(Path.Combine(root, "interface.json"));
        string version = definition["version"]?.GetValue<string>() ?? "unknown";
        string imageRoot = Path.Combine(root, "userdata", "resource", "image");
        string target = Path.Combine(imageRoot, "UserStages");
        var roots = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { Path.Combine(root, "resource") };
        if (definition["resource"] is JsonArray resources)
            foreach (JsonObject resource in resources.OfType<JsonObject>())
                if (resource["path"] is JsonArray paths)
                    foreach (var value in paths)
                    {
                        string? raw = value?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(raw))
                            roots.Add(Path.GetFullPath(Path.Combine(root, raw.Replace("{PROJECT_DIR}", root))));
                    }
        var legacy = roots.Select(path => Path.Combine(path, "image", "UserStages"))
            .Where(path => !string.Equals(Path.GetFullPath(path), target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Where(Directory.Exists).ToArray();
        var copies = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string folder in legacy)
            foreach (string file in Files(folder))
            {
                string relative = Path.GetRelativePath(folder, file);
                string destination = Path.Combine(target, relative);
                if (File.Exists(destination))
                {
                    if (!SameFile(file, destination)) throw new IOException("发现不同的同名关卡截图：" + relative);
                }
                else if (copies.TryGetValue(relative, out var previous) && !SameFile(file, previous))
                    throw new IOException("多个资源包含不同的同名关卡截图：" + relative);
                else copies[relative] = file;
            }

        // Validate existing references before writing a success marker. Never silently
        // replace a broken saved profile with a blank profile or default task list.
        var configs = Files(Path.Combine(root, "config"))
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        string appsettings = Path.Combine(root, "appsettings.json");
        if (File.Exists(appsettings)) configs.Add(appsettings);
        foreach (string config in configs)
            foreach (string text in Strings(JsonNode.Parse(File.ReadAllText(config), documentOptions: ReadOptions)))
            {
                string relative = text.Replace('\\', '/');
                if (!relative.StartsWith("UserStages/", StringComparison.Ordinal)) continue;
                string resolved = Path.GetFullPath(Path.Combine(imageRoot, relative));
                if (!resolved.StartsWith(target + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || (!File.Exists(resolved) && !copies.ContainsKey(Path.GetRelativePath(target, resolved))))
                    throw new IOException("已保存的关卡截图缺失：" + relative + "；配置保持原样，请从旧目录或备份恢复图片。");
            }

        string configPath = Path.Combine(root, "config", "config.json");
        var current = File.Exists(configPath) ? ReadObject(configPath) : new JsonObject();
        string maaOptionsPath = Path.Combine(root, "config", "maa_option.json");
        var maaOptions = File.Exists(maaOptionsPath) ? ReadObject(maaOptionsPath) : new JsonObject();
        bool enableErrorCapture = !JsonNode.DeepEquals(maaOptions["save_on_error"], JsonValue.Create(true));
        if (enableErrorCapture) maaOptions["save_on_error"] = true;
        var defaults = new JsonObject
        {
            ["EnableCheckVersion"] = true,
            ["TkfmForkUpdateCheckV1"] = true,
            ["DownloadSourceIndex"] = 0,
            ["EnableAutoUpdateResource"] = false,
            ["EnableAutoUpdateMFA"] = false
        };
        var profileEdits = new Dictionary<string, JsonObject>();
        string configDir = Path.GetDirectoryName(configPath)!;
        foreach (string path in configs.Where(path => Path.GetDirectoryName(path) == configDir
                     && Path.GetFileName(path).StartsWith("mfa_", StringComparison.OrdinalIgnoreCase)))
        {
            var profile = ReadObject(path);
            if (ApplyDesktopUiPolicy(profile)) profileEdits.Add(path, profile);
        }
        bool desktopUiChanged = ApplyDesktopUiPolicy(current);
        var instanceEdits = new Dictionary<string, JsonObject>();
        var launchDefinition = (definition["task"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(task => task["entry"]?.ToString() == "GameLoginStart");
        foreach (string file in configs.Where(path => Path.GetDirectoryName(path) == Path.Combine(root, "config", "instances")))
        {
            var instance = ReadObject(file);
            var original = instance.DeepClone();
            var tasks = instance["TaskItems"] as JsonArray;
            if (tasks != null) ShopOptionsMigration.Apply(instance, tasks, definition);
            var launch = tasks?.OfType<JsonObject>().FirstOrDefault(task => task["entry"]?.ToString() == "GameLoginStart");
            if (launchDefinition != null && tasks != null && instance["TkfmLaunchTaskFirst"]?.GetValue<bool>() != true)
            {
                // CurrentTasks is also MFA's history of tasks explicitly deleted by the user.
                bool removed = (instance["CurrentTasks"] as JsonArray)?.Any(item =>
                    item?.ToString().EndsWith("GameLoginStart", StringComparison.Ordinal) == true) == true;
                if (launch == null && !removed && tasks.Count > 0)
                {
                    launch = new JsonObject
                    {
                        ["name"] = launchDefinition["name"]?.DeepClone(), ["entry"] = "GameLoginStart",
                        ["default_check"] = true
                    };
                    tasks.Insert(0, launch);
                }
                else if (launch != null && tasks.IndexOf(launch) > 0)
                {
                    tasks.Remove(launch);
                    tasks.Insert(0, launch);
                }
                instance["TkfmLaunchTaskFirst"] = true;
            }
            if (launch != null)
            {
                launch["default_check"] = true;
                MigrateSchedule(instance, tasks!, launch);
                MigrateFastDispatch(instance, launch);
                MoveFastDispatchToTask(tasks!, launch);
            }
            if (!JsonNode.DeepEquals(instance, original)) instanceEdits.Add(file, instance);
        }
        bool missingDefaults = defaults.Any(pair => !current.ContainsKey(pair.Key));
        string marker = Path.Combine(root, ".tkfm-userdata.json");
        bool newVersion = !File.Exists(marker) || ReadObject(marker)["version"]?.GetValue<string>() != version;
        if (newVersion || copies.Count > 0 || missingDefaults || desktopUiChanged
            || enableErrorCapture || profileEdits.Count > 0 || instanceEdits.Count > 0)
        {
            string backup = Path.Combine(root, "backup", "userdata-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            CopyTree(Path.Combine(root, "config"), Path.Combine(backup, "config"));
            CopyTree(Path.Combine(root, "userdata"), Path.Combine(backup, "userdata"));
            if (File.Exists(appsettings))
            {
                Directory.CreateDirectory(backup);
                File.Copy(appsettings, Path.Combine(backup, "appsettings.json"));
            }
            for (int index = 0; index < legacy.Length; index++)
                CopyTree(legacy[index], Path.Combine(backup, "legacy-images", index.ToString()));
            WriteObject(Path.Combine(backup, "migration.json"), new JsonObject
            {
                ["version"] = version, ["legacy_roots"] = new JsonArray(legacy.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray())
            });
            Directory.CreateDirectory(target);
            foreach (var pair in copies)
            {
                string destination = Path.Combine(target, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(pair.Value, temp);
                    if (!SameFile(pair.Value, temp)) throw new IOException("截图迁移不完整：" + pair.Value);
                    File.Move(temp, destination);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            foreach (var pair in defaults)
                if (!current.ContainsKey(pair.Key)) current[pair.Key] = pair.Value?.DeepClone();
            if (missingDefaults || desktopUiChanged) WriteObject(configPath, current);
            if (enableErrorCapture) WriteObject(maaOptionsPath, maaOptions);
            foreach (var edit in profileEdits) WriteObject(edit.Key, edit.Value);
            foreach (var edit in instanceEdits) WriteObject(edit.Key, edit.Value);
            WriteObject(marker, new JsonObject { ["schema"] = 1, ["version"] = version, ["backup"] = backup });
        }
        Directory.CreateDirectory(target);
    }
}
