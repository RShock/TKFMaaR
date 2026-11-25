using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.UserControls.Settings;
using MFAAvalonia.Views.Windows;
using Microsoft.Win32;
using SukiUI.Controls;

public sealed class TkfmBackgroundSettings : IMaaCustomAction
{
    private const string ServiceKey = "TKFMaa.BackgroundSettings.Service";
    public string Name { get; set; } = "TkfmBackgroundSettings";

    public TkfmBackgroundSettings()
    {
        if (!OperatingSystem.IsWindows()) return;
        Dispatcher.UIThread.Post(() =>
        {
            (AppDomain.CurrentDomain.GetData(ServiceKey) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(ServiceKey, new TkfmBackgroundService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmBackgroundService : IDisposable
{
    private const string StartInTrayArgument = "--tkfm-start-in-tray";
    private const string StartupHandledKey = "TKFMaa.BackgroundSettings.StartupHandled";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const uint WindowClose = 0x0010;
    private const string RestartScript = @"
$ErrorActionPreference = 'Stop'
$previous = Get-Process -Id $env:TKFM_RESTART_PID -ErrorAction SilentlyContinue
if ($previous) {
    try {
        if ($previous.StartTime.ToUniversalTime().Ticks -eq [long]$env:TKFM_RESTART_STARTED) {
            $previous.WaitForExit()
        }
    } catch [System.InvalidOperationException] {
        # The old process exited before its handle could be opened.
    } finally {
        $previous.Dispose()
    }
}
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $env:TKFM_RESTART_EXECUTABLE
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($start.FileName)
$start.UseShellExecute = $false
Remove-Item Env:TKFM_RESTART_PID, Env:TKFM_RESTART_STARTED, Env:TKFM_RESTART_EXECUTABLE
$replacement = [Diagnostics.Process]::Start($start)
$replacement.Dispose()
";
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<StackPanel, StackPanel> settings = new Dictionary<StackPanel, StackPanel>();
    private readonly EventWaitHandle exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset,
        @"Local\TKFMaaR.DevExit." + Environment.ProcessId);
    private readonly IDisposable titleBarClick;
    private readonly Win32Properties.CustomWndProcHookCallback windowMessage;
    private readonly string executable = Path.GetFullPath(Environment.ProcessPath
        ?? throw new InvalidOperationException("找不到助手程序路径"));
    private RootView? window;
    private NativeMenu? trayMenu;
    private NativeMenuItem? restartItem;
    private bool restartRequested;
    private bool disposed;

    internal TkfmBackgroundService()
    {
        windowMessage = HandleWindowMessage;
        titleBarClick = Button.ClickEvent.AddClassHandler<Button>(HandleTitleBarClick);
        EnforceTaskbarMinimize();
        ConfigurationManager.ConfigurationSwitched += EnforceTaskbarMinimize;
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private bool CanHide() => !disposed && window != null && Application.Current is { } app
        && TrayIcon.GetIcons(app)?.Any(icon => icon.IsVisible) == true;

    private static void EnforceTaskbarMinimize(string? _ = null)
    {
        var config = ConfigurationManager.Current;
        if (config.GetValue(ConfigurationKeys.ShouldMinimizeToTray, false))
            config.SetValue(ConfigurationKeys.ShouldMinimizeToTray, false);
    }

    private void HideToTray()
    {
        if (CanHide()) Instances.RootViewModel.IsWindowVisible = false;
    }

    // Suki's title-bar button calls Window.Close() directly. Handle its routed click
    // before that call, including when MFA's running-task confirmation is active.
    private void HandleTitleBarClick(Button button, RoutedEventArgs e)
    {
        if (disposed || window == null || button.Name != "PART_CloseButton"
            || !ReferenceEquals(TopLevel.GetTopLevel(button), window)) return;
        e.Handled = true;
        HideToTray();
    }

    private IntPtr HandleWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Alt+F4 and the taskbar close command arrive here. Avalonia's explicit
        // lifetime shutdown from the native tray menu still takes its usual path.
        if (message == WindowClose && !disposed)
        {
            handled = true;
            Dispatcher.UIThread.Post(HideToTray);
        }
        return IntPtr.Zero;
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed || restartRequested) return;
        if (window == null)
        {
            if (!(Instances.TopLevel is RootView root) || !root.IsLoaded) return;
            Win32Properties.AddWndProcHookCallback(root, windowMessage);
            window = root;
            window.Closed += WindowClosed;
        }

        RefreshTrayMenu();

        if (CanHide() && AppDomain.CurrentDomain.GetData(StartupHandledKey) == null
            && Environment.GetCommandLineArgs().Contains(StartInTrayArgument, StringComparer.OrdinalIgnoreCase))
        {
            AppDomain.CurrentDomain.SetData(StartupHandledKey, true);
            HideToTray();
        }

        if (exitRequest.WaitOne(0) && CanExitForDevReload())
        {
            // Synchronous close on the UI thread prevents a scheduler tick from
            // claiming a new task between the idle check and the close request.
            window.Close();
            return;
        }

        foreach (var view in window.GetVisualDescendants().OfType<GuiSettingsUserControl>())
        {
            if (view.Content is not StackPanel host) continue;
            HideMinimizeToTrayCard(host);
            if (!settings.ContainsKey(host)) InstallSettings(host);
        }
    }

    private void RefreshTrayMenu()
    {
        var menu = Application.Current is { } app ? TrayIcon.GetIcons(app)?.FirstOrDefault()?.Menu : null;
        if (ReferenceEquals(trayMenu, menu)) return;
        RemoveRestartItem();
        if (menu == null) return;

        var item = new NativeMenuItem { Header = "重启" };
        item.Click += RestartFromTray;
        // MFAAvalonia places its Quit command last in the native tray menu.
        menu.Items.Insert(Math.Max(0, menu.Items.Count - 1), item);
        trayMenu = menu;
        restartItem = item;
    }

    private void RemoveRestartItem()
    {
        if (restartItem == null) return;
        restartItem.Click -= RestartFromTray;
        trayMenu?.Items.Remove(restartItem);
        restartItem = null;
        trayMenu = null;
    }

    private void RestartFromTray(object? sender, EventArgs args)
    {
        if (disposed || restartRequested || window == null) return;
        restartRequested = true;

        try
        {
            // Keep the development environment and wait for the old process to
            // release its runtime and configuration before reopening the frontend.
            using var current = Process.GetCurrentProcess();
            var launcher = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            launcher.ArgumentList.Add("-NoProfile");
            launcher.ArgumentList.Add("-NonInteractive");
            launcher.ArgumentList.Add("-EncodedCommand");
            launcher.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(RestartScript)));
            launcher.Environment["TKFM_RESTART_PID"] = current.Id.ToString(CultureInfo.InvariantCulture);
            launcher.Environment["TKFM_RESTART_STARTED"] = current.StartTime.ToUniversalTime().Ticks
                .ToString(CultureInfo.InvariantCulture);
            launcher.Environment["TKFM_RESTART_EXECUTABLE"] = executable;
            using var handoff = Process.Start(launcher) ?? throw new InvalidOperationException("无法启动重启进程");
        }
        catch (Exception ex)
        {
            restartRequested = false;
            LoggerHelper.Error($"托盘重启失败：{ex.Message}", ex);
            Instances.RootViewModel.IsWindowVisible = true;
            ToastHelper.Error("重启失败", ex.Message);
            return;
        }

        // A paused callback or a stuck task must not keep the old process alive.
        Instances.EnsureShutdownWatchdogStarted();
        window.ShutdownAfterConfirmedExit();
    }

    private static bool CanExitForDevReload()
    {
        var scheduledOperationBusy = AppDomain.CurrentDomain.GetData("TKFMaa.Dispatch.Schedule.IsBusy") as Func<bool>;
        return !Instances.RootViewModel.IsRunning && scheduledOperationBusy?.Invoke() != true
            && !MaaProcessor.Processors.ToArray().Any(processor => processor.IsTaskRunActive
                || processor.IsConnecting || processor.MaaTasker?.IsRunning == true || processor.TaskQueue.Count > 0);
    }

    private static void HideMinimizeToTrayCard(StackPanel host)
    {
        string title = LanguageHelper.GetLocalizedString(LangKeys.MinimizeToTray);
        string description = LanguageHelper.GetLocalizedString(LangKeys.MinimizeToTrayDescription);
        foreach (var card in host.Children.OfType<GlassCard>())
        {
            if (card.Content is not DockPanel row || !row.Children.OfType<ToggleSwitch>().Any()) continue;
            if (row.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>())
                .Any(label => label.Text == title || label.Text == description))
                card.IsVisible = false;
        }
    }

    private void InstallSettings(StackPanel host)
    {
        var startup = new ToggleSwitch { Content = "开机自启动（登录 Windows 后）" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var section = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 10) };
        section.Children.Add(startup);
        section.Children.Add(error);
        settings.Add(host, section);
        host.Children.Insert(0, section);

        string startupName = "TKFMaaR_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant()))).Substring(0, 16);
        string command = "\"" + executable + "\" " + StartInTrayArgument;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            startup.IsChecked = string.Equals(key?.GetValue(startupName) as string, command,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            startup.IsEnabled = false;
            error.Text = "无法读取开机自启动设置。" + ex.Message;
            error.IsVisible = true;
        }
        bool selected = startup.IsChecked == true;
        bool restoring = false;
        startup.IsCheckedChanged += (_, _) =>
        {
            if (restoring) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (startup.IsChecked == true) key.SetValue(startupName, command, RegistryValueKind.String);
                else key.DeleteValue(startupName, throwOnMissingValue: false);
                selected = startup.IsChecked == true;
                error.IsVisible = false;
            }
            catch (Exception ex)
            {
                restoring = true;
                startup.IsChecked = selected;
                restoring = false;
                error.Text = "无法修改开机自启动设置。" + ex.Message;
                error.IsVisible = true;
            }
        };
    }

    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        RemoveRestartItem();
        ConfigurationManager.ConfigurationSwitched -= EnforceTaskbarMinimize;
        titleBarClick.Dispose();
        exitRequest.Dispose();
        if (window != null)
        {
            window.Closed -= WindowClosed;
            Win32Properties.RemoveWndProcHookCallback(window, windowMessage);
        }
        foreach (var (host, section) in settings) host.Children.Remove(section);
        settings.Clear();
    }
}
