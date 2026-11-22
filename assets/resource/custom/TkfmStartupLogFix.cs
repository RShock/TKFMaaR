using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;

// Loaded by MFAAvalonia's existing custom-action compiler. Never runs a game action.
public sealed class TkfmStartupLogFix : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmStartupLogFix";

    public TkfmStartupLogFix()
    {
        // Apply before onlyStart completes and the frontend warms up preview.
        TkfmStartupLogFixService.ConfigureSharedCapture();
        // Consume the auto-connect onlyStart completion synchronously. Waiting for
        // the UI dispatcher can race with the short startup connection task.
        TkfmStartupLogFixService.SuppressCurrentStartupCompletion();
        // Resource loading precedes SetConnected. Install the handlers before returning.
        const string key = "TKFMaa.StartupLogFix.Service";
        lock (string.Intern(key))
        {
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmStartupLogFixService());
        }
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmStartupLogFixService : IDisposable
{
    private const string SeparateCaptureSetting = "UseSeparateScreenshotTasker";
    private static readonly FieldInfo? CompletionHandledField = typeof(MaaProcessor).GetField(
        "_stopCompletionMessageHandled", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? LiveViewNoImageLoggedField = typeof(TaskQueueViewModel).GetField(
        "_liveViewNoImageLogged", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? LiveTimerField = typeof(TaskQueueViewModel).GetField(
        "_liveViewTimer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? LiveTickMethod = typeof(TaskQueueViewModel).GetMethod(
        "OnLiveViewTimerElapsed", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly object sync = new object();
    private readonly Dictionary<MaaProcessor, PreviewGuard> processors = new();
    private DispatcherTimer? timer;
    private bool disposed;

    internal TkfmStartupLogFixService()
    {
        Refresh(null, EventArgs.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            lock (sync)
            {
                if (disposed) return;
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += Refresh;
                timer.Start();
            }
        });
    }
    internal static void SuppressCurrentStartupCompletion()
    {
        try
        {
            // This extension is instantiated by the same onlyStart resource-loading pass
            // that previously ended with a false success message. The next user StartTask
            // resets this flag in MFAAvalonia before running a real queue.
            foreach (var processor in MaaProcessor.Processors.ToArray())
                SetIntField(processor, CompletionHandledField, 1);
        }
        catch
        {
            // Keep startup connection behavior unchanged if the frontend field is absent.
        }
    }

    internal static void ConfigureSharedCapture()
    {
        foreach (var processor in MaaProcessor.Processors.ToArray())
        {
            try
            {
                // Hot reload must not switch controllers underneath a running
                // or paused native task. Apply on its next idle pass instead.
                if (processor.MaaTasker?.IsRunning == true) continue;
                if (!processor.InstanceConfiguration.GetValue(SeparateCaptureSetting, true)) continue;
                processor.InstanceConfiguration.SetValue(SeparateCaptureSetting, false);
                LoggerHelper.Info("TKFMaa：任务识别与实时预览共用截图控制器，已关闭独立预览连接。");
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning("TKFMaa 共享截图配置暂未应用：" + ex.Message);
            }
        }
    }

    private void Refresh(object? sender, EventArgs args)
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                ConfigureSharedCapture();
                var active = MaaProcessor.Processors.ToArray();
                foreach (var processor in active)
                {
                    var vm = processor.ViewModel;
                    processors.TryGetValue(processor, out var old);
                    if (ReferenceEquals(old?.ViewModel, vm)) continue;
                    old?.Dispose();
                    processors.Remove(processor);
                    if (vm != null && LiveViewNoImageLoggedField?.FieldType == typeof(bool)
                        && LiveTimerField?.GetValue(vm) is System.Timers.Timer poller
                        && LiveTickMethod != null)
                    {
                        var original = (System.Timers.ElapsedEventHandler)LiveTickMethod.CreateDelegate(
                            typeof(System.Timers.ElapsedEventHandler), vm);
                        processors[processor] = new PreviewGuard(processor, vm, poller, original);
                    }
                }
                foreach (var removed in processors.Keys.Except(active).ToArray())
                {
                    processors[removed].Dispose();
                    processors.Remove(removed);
                }
            }
            catch { /* Optional compatibility fix; never block the connection. */ }
        }
    }
    private static void SetBoolField(object? target, FieldInfo? field, bool value)
    {
        if (target == null || field == null || field.FieldType != typeof(bool)) return;
        try { field.SetValue(target, value); }
        catch { }
    }

    private static void SetIntField(object? target, FieldInfo? field, int value)
    {
        if (target == null || field == null || field.FieldType != typeof(int)) return;
        try { field.SetValue(target, value); }
        catch { }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            var oldTimer = timer;
            if (oldTimer != null) Dispatcher.UIThread.Post(() => { oldTimer.Stop(); oldTimer.Tick -= Refresh; });
            foreach (var guard in processors.Values) guard.Dispose();
            processors.Clear();
        }
    }

    private sealed class PreviewGuard : IDisposable
    {
        internal readonly TaskQueueViewModel ViewModel;
        private readonly MaaProcessor processor;
        private readonly System.Timers.Timer poller;
        private readonly System.Timers.ElapsedEventHandler original;
        private readonly object gate = new object();
        private bool disposed;
        private bool wasExecuting;

        internal PreviewGuard(MaaProcessor processor, TaskQueueViewModel vm, System.Timers.Timer poller,
            System.Timers.ElapsedEventHandler original)
        {
            this.processor = processor;
            ViewModel = vm;
            this.poller = poller;
            this.original = original;
            SetBoolField(ViewModel, LiveViewNoImageLoggedField, true);
            poller.Elapsed -= original;
            poller.Elapsed += Tick;
        }

        private void Tick(object? sender, System.Timers.ElapsedEventArgs args)
        {
            // Avalonia can receive another timer tick while the preceding frame is in flight.
            if (!System.Threading.Monitor.TryEnter(gate)) return;
            try
            {
                if (disposed) return;
                bool executing = processor.IsTaskRunActive && processor.MaaTasker?.IsRunning == true;
                // The native poller clears this flag on every successful frame. Apply
                // the idle policy at the actual point of use so a later gap stays quiet.
                if (!executing || !wasExecuting)
                    SetBoolField(ViewModel, LiveViewNoImageLoggedField, !executing);
                wasExecuting = executing;
                original(sender, args);
            }
            catch (ObjectDisposedException) { /* The device can be released while a tick is queued. */ }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                poller.Elapsed -= Tick;
                poller.Elapsed += original;
                SetBoolField(ViewModel, LiveViewNoImageLoggedField, false);
            }
        }
    }
}
