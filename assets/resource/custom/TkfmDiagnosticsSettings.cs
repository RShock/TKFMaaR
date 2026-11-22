using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls.Settings;
using SukiUI.Controls;
using SukiUI.Toasts;

public sealed class TkfmDiagnosticsSettings : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDiagnosticsSettings";

    public TkfmDiagnosticsSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.DiagnosticsSettings.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmDiagnosticsSettingsService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmDiagnosticsSettingsService : IDisposable
{
    private const string HiddenConnectionToast = "tkfm-hidden-connection-toast";
    private readonly ISukiToastManager toasts = Instances.ToastManager;
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };

    internal TkfmDiagnosticsSettingsService()
    {
        toasts.OnToastQueued += OnToastQueued;
        if (Instances.TopLevel is { } root)
            foreach (var host in root.GetVisualDescendants().OfType<SukiToastHost>())
                foreach (var toast in host.Items.OfType<ISukiToast>().ToArray())
                    FilterToast(toast);
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs e)
    {
        if (Instances.TopLevel is not { } root) return;
        try
        {
            var gameSettings = Instances.GameSettingsUserControlModel;
            if (!gameSettings.EnableSaveOnError) gameSettings.EnableSaveOnError = true;

            foreach (var view in root.GetVisualDescendants().OfType<SettingsView>())
                AddSettingsHeader(view);

            foreach (var view in root.GetVisualDescendants().OfType<GameSettingsUserControl>())
            {
                var label = view.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => text.Text == LangKeys.EnableSaveOnError.ToLocalization());
                var row = label?.GetVisualAncestors().OfType<DockPanel>().FirstOrDefault();
                if (row != null) row.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("TKFMaa 故障诊断界面更新失败", ex);
            timer.Stop();
        }
    }

    private void OnToastQueued(object sender, SukiToastQueuedEventArgs e) => FilterToast(e.Toast);

    private void FilterToast(ISukiToast toast)
    {
        if (toast is not Control control) return;
        bool hide = toast.Title == LangKeys.ConnectFailed.ToLocalization()
            || toast.Title == LangKeys.Warning_CannotConnect.ToLocalizationFormatted(true, LangKeys.Emulator)
            || toast.Title == LangKeys.Warning_CannotConnect.ToLocalizationFormatted(true, LangKeys.Window)
            || toast.Title == LangKeys.Warning_CannotConnect.ToLocalizationFormatted(true, LangKeys.TabPlayCover)
            || toast.Title == LangKeys.ConnectingEmulatorOrWindow.ToLocalizationFormatted(true, LangKeys.Emulator)
            || toast.Title == LangKeys.ConnectingEmulatorOrWindow.ToLocalizationFormatted(true, LangKeys.Window);
        if (hide)
        {
            if (!control.Classes.Contains(HiddenConnectionToast)) control.Classes.Add(HiddenConnectionToast);
            control.IsVisible = false;
            // Queue subscribers run synchronously. Dismiss after the host registers the toast.
            Dispatcher.UIThread.Post(() =>
            {
                if (control.Classes.Contains(HiddenConnectionToast) && !toasts.IsDismissed(toast))
                    toasts.Dismiss(toast);
            }, DispatcherPriority.Send);
        }
        else if (control.Classes.Contains(HiddenConnectionToast))
        {
            control.Classes.Remove(HiddenConnectionToast);
            // Suki pools toast controls. Do not leave a recycled control hidden.
            control.ClearValue(Visual.IsVisibleProperty);
        }
    }

    private static void AddSettingsHeader(SettingsView view)
    {
        if (view.Content is not Grid grid || grid.RowDefinitions.Count != 0) return;
        var title = grid.Children.OfType<TextBlock>().FirstOrDefault();
        var content = grid.Children.OfType<SukiStackPage>().FirstOrDefault();
        if (title == null || content == null) return;

        var export = new Button { Content = "导出错误日志", HorizontalAlignment = HorizontalAlignment.Left };
        export.Click += async (_, _) => await ExportAsync(export);
        var header = new StackPanel
        {
            Margin = new Thickness(20, 8, 20, 0),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "这些设置基于MAA原版框架，大部分我用不到，所以没有测试。",
                    TextWrapping = TextWrapping.Wrap
                },
                export
            }
        };
        grid.RowDefinitions = new RowDefinitions("Auto,Auto,*");
        Grid.SetRow(header, 1);
        Grid.SetRow(content, 2);
        grid.Children.Insert(1, header);
    }

    private static async Task ExportAsync(Button button)
    {
        button.IsEnabled = false;
        button.Content = "正在导出…";
        try
        {
            if (AppDomain.CurrentDomain.GetData("TKFMaa.ExportErrorLogs") is not Func<string, string, string> export
                || AppDomain.CurrentDomain.GetData("TKFMaa.RevealErrorLogs") is not Action<string> reveal)
                throw new InvalidOperationException("当前桌面运行环境缺少日志导出组件。");
            string logsDirectory = AppPaths.LogsDirectory;
            string installRoot = AppPaths.InstallRoot;
            string archive = await Task.Run(() => export(logsDirectory, installRoot));
            try
            {
                reveal(archive);
                ToastHelper.Success("错误日志已导出", System.IO.Path.GetFileName(archive));
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning("无法定位已导出的错误日志：" + ex.Message);
                ToastHelper.Warn("错误日志已导出", archive);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("TKFMaa 导出错误日志失败", ex);
            ToastHelper.Error("导出错误日志失败", ex.Message);
        }
        finally
        {
            button.Content = "导出错误日志";
            button.IsEnabled = true;
        }
    }

    public void Dispose()
    {
        toasts.OnToastQueued -= OnToastQueued;
        timer.Stop();
        timer.Tick -= Refresh;
    }
}
