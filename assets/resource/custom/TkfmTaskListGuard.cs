using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;

// Delay custom visual-tree work while the native task list is rebuilding its item containers.
public sealed class TkfmTaskListGuard : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmTaskListGuard";

    public TkfmTaskListGuard()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.TaskListGuard.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmTaskListGuardService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmTaskListGuardService : IDisposable
{
    private const string BusyKey = "TKFMaa.TaskListGuard.IsBusy";
    private static readonly TimeSpan RebuildGracePeriod = TimeSpan.FromMilliseconds(1200);
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<TaskQueueViewModel, NotifyCollectionChangedEventHandler> subscriptions =
        new Dictionary<TaskQueueViewModel, NotifyCollectionChangedEventHandler>();
    private readonly Func<bool> busyCallback;
    private DateTime busyUntil;
    private bool disposed;

    internal TkfmTaskListGuardService()
    {
        busyCallback = () => !disposed && DateTime.UtcNow < busyUntil;
        AppDomain.CurrentDomain.SetData(BusyKey, busyCallback);
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed) return;
        try
        {
            var current = new HashSet<TaskQueueViewModel>();
            foreach (var processor in MaaProcessor.Processors.ToArray())
            {
                var viewModel = processor.ViewModel;
                if (viewModel == null) continue;
                current.Add(viewModel);
                if (subscriptions.ContainsKey(viewModel)) continue;
                NotifyCollectionChangedEventHandler handler = (_, __) =>
                    busyUntil = DateTime.UtcNow.Add(RebuildGracePeriod);
                viewModel.TaskItemViewModels.CollectionChanged += handler;
                subscriptions[viewModel] = handler;
            }

            foreach (var viewModel in subscriptions.Keys.Where(viewModel => !current.Contains(viewModel)).ToArray())
            {
                viewModel.TaskItemViewModels.CollectionChanged -= subscriptions[viewModel];
                subscriptions.Remove(viewModel);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning("TKFMaa 任务列表变更保护刷新失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var pair in subscriptions)
            pair.Key.TaskItemViewModels.CollectionChanged -= pair.Value;
        subscriptions.Clear();
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData(BusyKey), busyCallback))
            AppDomain.CurrentDomain.SetData(BusyKey, null);
    }
}


