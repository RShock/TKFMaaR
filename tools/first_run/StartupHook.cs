using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// .NET calls this before MFA reads appsettings.json or creates configuration objects.
internal static class StartupHook
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string title, uint flags);

    public static void Initialize()
    {
        string root = AppContext.BaseDirectory;
        AppDomain.CurrentDomain.SetData("TKFMaa.ExportErrorLogs", new Func<string, string, string>(ErrorLogExport.Create));
        AppDomain.CurrentDomain.SetData("TKFMaa.RevealErrorLogs", new Action<string>(ErrorLogExport.Reveal));
        AppDomain.CurrentDomain.SetData("TKFMaa.ResolveEmulatorConnection", new Func<string, string, string, bool, string, string, string[]>(EmulatorConnectionDefaults.Resolve));
        string marker = Path.Combine(root, ".tkfm-first-run-migration.json");
        try
        {
            FileStream guard;
            try
            {
                guard = new FileStream(Path.Combine(root, ".tkfm-first-run-migration.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { Environment.Exit(0); return; }
            using (guard)
            {
                // An importer can outlive a forcibly closed frontend. Never let a
                // second frontend load half-replaced settings during that window.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var importGuard = new FileStream(Path.Combine(root, ".tkfm-first-run-import.lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException) when (attempt < 100) { Thread.Sleep(100); }
                    catch (IOException)
                    {
                        ShowError("旧版配置仍在导入，请等待迁移完成后再打开助手。");
                        Environment.Exit(0);
                        return;
                    }
                }
                // Normal installation and same-directory updates are automatic.
                // The legacy wizard is now an explicit cross-directory import only.
                string request = Path.Combine(root, ".tkfm-import-request");
                if (!OperatingSystem.IsWindows() || !File.Exists(request))
                {
                    UserDataMigration.Apply(root);
                    ScheduleBootstrap.Start(root);
                    return;
                }
                string script = Path.Combine(root, "migration", "first_run.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("缺少首次启动迁移向导", script);
                // Separate from config/: importing old settings cannot restore the prompt.
                using (var state = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.Read))
                    JsonSerializer.Serialize(state, new { version = 1, status = "starting", at = DateTimeOffset.Now });
                string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PowerShell", "7", "pwsh.exe");
                string configuredShell = Environment.GetEnvironmentVariable("TKFM_FIRST_RUN_PWSH") ?? "";
                string? pathShell = (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "pwsh.exe")).FirstOrDefault(File.Exists);
                string shell = File.Exists(configuredShell) ? configuredShell : File.Exists(pwsh) ? pwsh : pathShell ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(shell)
                {
                    WorkingDirectory = root, UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                foreach (string arg in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-InstallRoot", root.TrimEnd(Path.DirectorySeparatorChar), "-CurrentProcessId", Environment.ProcessId.ToString() })
                    start.ArgumentList.Add(arg);
                using var wizard = Process.Start(start) ?? throw new IOException("无法打开迁移向导");
                var output = wizard.StandardOutput.ReadToEndAsync();
                var error = wizard.StandardError.ReadToEndAsync();
                wizard.WaitForExit();
                if (wizard.ExitCode != 0)
                {
                    string log = Path.Combine(root, "migration", "startup-error.log");
                    File.WriteAllText(log, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                    throw new IOException("迁移向导未正常结束，错误详情：" + log);
                }
                File.Delete(request);
                UserDataMigration.Apply(root);
                ScheduleBootstrap.Start(root);
            }
        }
        catch (Exception ex)
        {
            ShowError("配置自动迁移未完成：" + ex.Message +
                "\n已停止启动，避免用不完整配置运行。原资料及 backup/ 备份保留，请按配置迁移说明恢复。");
            Environment.Exit(1);
        }
    }

    private static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "TKFMaaR", 0x30);
        else Console.Error.WriteLine(message);
    }
}
