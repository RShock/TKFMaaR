using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;

public sealed class TkfmStaminaPotionExpiry : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmStaminaPotionExpiry";

    public TkfmStaminaPotionExpiry()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.StaminaPotionExpiry.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmStaminaPotionExpiryService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmStaminaPotionExpiryService : IDisposable
{
    private const string RecognitionParameters = """
        {"all_of":["__TkfmStaminaPotionExpiryRedBanner","__TkfmStaminaPotionExpiryHourGlyph"]}
        """;
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<MaaProcessor, EventHandler<ObservableQueue<MFATask>.CountChangedEventArgs>> handlers =
        new Dictionary<MaaProcessor, EventHandler<ObservableQueue<MFATask>.CountChangedEventArgs>>();
    private readonly ConditionalWeakTable<MFATask, object> wrapped = new ConditionalWeakTable<MFATask, object>();
    private readonly List<(MaaProcessor Processor, DragItemViewModel Item, long RunId)> pending =
        new List<(MaaProcessor, DragItemViewModel, long)>();
    private volatile bool disposed;

    internal TkfmStaminaPotionExpiryService()
    {
        MaaProcessor.Processors.CollectionChanged += OnProcessorsChanged;
        OnProcessorsChanged(null, null);
        timer.Tick += FlushWarnings;
        timer.Start();
    }

    private void OnProcessorsChanged(object? sender, NotifyCollectionChangedEventArgs? args)
    {
        foreach (var processor in handlers.Keys.Where(processor => !MaaProcessor.Processors.Contains(processor)).ToArray())
        {
            processor.TaskQueue.CountChanged -= handlers[processor];
            handlers.Remove(processor);
            pending.RemoveAll(warning => ReferenceEquals(warning.Processor, processor));
        }
        foreach (var processor in MaaProcessor.Processors)
        {
            if (handlers.ContainsKey(processor)) continue;
            EventHandler<ObservableQueue<MFATask>.CountChangedEventArgs> handler = (_, change) =>
            {
                if (!disposed && change.NewValue > change.OldValue) WrapQueuedTasks(processor);
            };
            handlers.Add(processor, handler);
            processor.TaskQueue.CountChanged += handler;
            WrapQueuedTasks(processor);
        }
    }

    private void WrapQueuedTasks(MaaProcessor processor)
    {
        // CountChanged fires inside the queue's reentrant lock. Any lets us inspect the
        // queued objects in place without adding work to the queue or changing its order.
        processor.TaskQueue.Any(task =>
        {
            if (task.SourceItem is { IsResourceOptionItem: false } item && task.RunId > 0
                && task.MaaAction is { } original && wrapped.TryAdd(task, new object()))
            {
                var checkedForExpiry = false;
                var runId = task.RunId;
                task.MaaAction = async () =>
                {
                    if (!checkedForExpiry && !disposed && processor.CancellationTokenSource?.IsCancellationRequested != true
                        && !processor.TaskQueue.Any(queued => queued.RunId == runId && queued.SourceItem != null))
                    {
                        checkedForExpiry = true;
                        if (await Task.Run(() => RecognizeExpiry(processor)))
                            RememberAfterTask(processor, item, runId);
                    }
                    return await original();
                };
            }
            return false;
        });
    }

    private static bool RecognizeExpiry(MaaProcessor processor)
    {
        try
        {
            var tasker = processor.MaaTasker;
            var controller = tasker?.Controller;
            if (tasker == null || controller == null || processor.CancellationTokenSource?.IsCancellationRequested == true)
                return false;
            using var image = new MaaImageBuffer();
            if (controller.Screencap().Wait() != MaaJobStatus.Succeeded || !controller.GetCachedImage(image)
                || processor.CancellationTokenSource?.IsCancellationRequested == true)
                return false;
            var job = tasker.AppendRecognition("And", RecognitionParameters, image);
            if (job.Wait() != MaaJobStatus.Succeeded
                || !tasker.GetTaskDetail(job.Id, out _, out var nodeIds, out _)) return false;
            // Recognition jobs can succeed even when no visual match is found.
            return nodeIds.Any(id => tasker.GetNodeDetail(id, out _, out _, out _, out var hit) && hit);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning("TKFMaa 体力药水提醒识别失败：" + ex.Message);
            return false;
        }
    }

    private void RememberAfterTask(MaaProcessor processor, DragItemViewModel item, long runId)
    {
        if (disposed) return;
        PropertyChangedEventHandler? onStateChanged = null;
        onStateChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(DragItemViewModel.RunState) || item.RunState == TaskRunState.Running)
                return;
            item.PropertyChanged -= onStateChanged;
            if (!disposed && item.RunId == runId
                && item.RunState is (TaskRunState.Succeeded or TaskRunState.Failed))
                pending.Add((processor, item, runId));
        };
        item.PropertyChanged += onStateChanged;
    }

    private void FlushWarnings(object? sender, EventArgs args)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var warning = pending[i];
            if (warning.Processor.IsTaskRunActive && warning.Item.RunId == warning.RunId) continue;
            pending.RemoveAt(i);
            warning.Processor.AddLog("体力药水即将过期，请自行处理！", Brushes.Red, changeColor: false);
        }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        timer.Tick -= FlushWarnings;
        MaaProcessor.Processors.CollectionChanged -= OnProcessorsChanged;
        foreach (var (processor, handler) in handlers) processor.TaskQueue.CountChanged -= handler;
        handlers.Clear();
        pending.Clear();
    }
}
