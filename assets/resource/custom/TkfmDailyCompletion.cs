using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class TkfmDailyCompletion : IMaaCustomAction
{
    private const string ServiceKey = "TKFMaa.DailyCompletion.Service";
    public string Name { get; set; } = "TkfmDailyCompletion";

    public TkfmDailyCompletion()
    {
        Dispatcher.UIThread.Post(() =>
        {
            (AppDomain.CurrentDomain.GetData(ServiceKey) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(ServiceKey, new TkfmDailyCompletionService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        if (args.NodeName != "DailyGuildRewardStart") return true;
        var processor = MaaProcessor.Processors.ToArray().FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
        if (processor == null || context.IsCancellationRequested) return false;
        try
        {
            if (!context.GetNodeData(args.NodeName, out var raw)) return false;
            bool weekendsOnly = !JToken.DeepEquals(JObject.Parse(raw)["attach"]?["weekends_only"], new JValue(false));
            bool skip = weekendsOnly && DateTime.Today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            DispatcherHelper.RunOnMainThread(() =>
            {
                if (AppDomain.CurrentDomain.GetData(ServiceKey) is not TkfmDailyCompletionService service)
                    throw new InvalidOperationException("任务状态扩展尚未就绪");
                service.RecordGuildRewardDecision(processor, skip);
            });
            if (context.IsCancellationRequested) return false;
            if (skip)
            {
                processor.AddLog("[领取协会奖励] 当前为工作日，按设置跳过。", changeColor: false);
                return true; // Keep the queue running. The status service maps this result to Skipped.
            }
            var result = context.RunTask("DailyGuildRewardRun", "{}");
            return !context.IsCancellationRequested && result?.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            processor.AddLog("[领取协会奖励] 无法执行任务：" + ex.Message, "OrangeRed");
            return false;
        }
    }
}

internal sealed class TkfmDailyCompletionService : IDisposable
{
    private static bool TaskListBusy() =>
        (AppDomain.CurrentDomain.GetData("TKFMaa.TaskListGuard.IsBusy") as Func<bool>)?.Invoke() == true;

    private const string GuildRewardTask = "领取协会奖励";
    private const string CanContinueKey = "TKFMaa.DailyCompletion.CanContinue";
    private const string ContinueKey = "TKFMaa.DailyCompletion.Continue";
    private const string Setting = "TkfmTaskRunHistoryV2";
    private const string LegacySetting = "TkfmDailyCompletionV1";
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<MaaProcessor, JObject> records = new Dictionary<MaaProcessor, JObject>();
    private readonly Dictionary<DragItemViewModel, MaaProcessor> tasks = new Dictionary<DragItemViewModel, MaaProcessor>();
    private readonly Dictionary<INotifyCollectionChanged, MaaProcessor> collections =
        new Dictionary<INotifyCollectionChanged, MaaProcessor>();
    private readonly Dictionary<DragItemViewModel, long> guildRewardSkips = new();
    private readonly Dictionary<Grid, (ColumnDefinition Column, TextBlock Text, StatusBadge? Badge)> labels = new();
    private readonly record struct StatusBadge(string Text, string? NonTodayDate = null, string Prefix = "", IBrush? Color = null);
    private bool reportedError;

    internal TkfmDailyCompletionService()
    {
        AppDomain.CurrentDomain.SetData(CanContinueKey, (Func<MaaProcessor, bool>)(processor => RemainingForToday(processor).Count > 0));
        AppDomain.CurrentDomain.SetData(ContinueKey, (Action<MaaProcessor>)ContinueToday);
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    // InterfaceItem.Name is MFA's task ID, independent of its optional display-name override.
    private static string? TaskKey(DragItemViewModel task) => task.IsResourceOptionItem
        ? null
        : task.InterfaceItem?.Name;

    private static string? HistoryKey(TaskQueueViewModel vm, DragItemViewModel task)
    {
        if (TaskKey(task) is not { Length: > 0 } name) return null;
        var sameName = vm.TaskItemViewModels.Where(item => TaskKey(item) == name).ToArray();
        if (sameName.Length < 2) return name;

        // Different configured copies of one Interface task must not share a completion.
        var signature = TaskSignature(task);
        int occurrence = sameName.TakeWhile(item => !ReferenceEquals(item, task))
            .Count(item => TaskSignature(item) == signature);
        return name + "\n" + signature + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
    }

    private static string TaskSignature(DragItemViewModel task)
    {
        var item = task.InterfaceItem!;
        var serialized = JsonConvert.SerializeObject(new { item.DisplayNameOverride, item.Option });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    private List<DragItemViewModel> RemainingForToday(MaaProcessor processor)
    {
        var pending = new List<DragItemViewModel>();
        if (TaskListBusy() || processor.ViewModel is not { } vm || processor.IsClosed || processor.IsConnecting
            || processor.IsTaskRunActive || processor.TaskQueue.Count != 0 || processor.MaaTasker?.IsRunning == true
            || vm.IsRunning || !vm.ToggleEnable) return pending;

        var today = DateTime.Today;
        var controller = vm.GetCurrentControllerName();
        var history = RecordFor(processor);
        bool hasCompleted = false;
        foreach (var item in vm.TaskItemViewModels)
        {
            if ((!item.IsChecked && item.IsCheckedWithNull.HasValue) || !item.SupportsResource(vm.CurrentResource)
                || !item.SupportsController(controller) || HistoryKey(vm, item) is not { } key) continue;
            if (TaskKey(item) == GuildRewardTask && today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                && item.InterfaceItem?.Option?.FirstOrDefault(option => option.Name == "仅在周六周日开启")?.Index != 1)
                continue;

            var record = history[key] as JObject;
            if (record != null && ReadSuccessTime(record)?.ToLocalTime().Date == today)
                hasCompleted = true;
            else
                pending.Add(item);
        }
        if (!hasCompleted) pending.Clear();
        return pending;
    }

    private void ContinueToday(MaaProcessor processor)
    {
        try
        {
            // Re-evaluate at click time because the selection, day, or queue may have changed.
            var pending = RemainingForToday(processor);
            if (pending.Count == 0) return;
            processor.AddLog($"[继续任务] 按当前顺序执行今天剩余的 {pending.Count} 项任务。", changeColor: false);
            processor.Start(pending);
        }
        catch (Exception ex) { processor.AddLog("[继续任务] " + ex.Message, "OrangeRed"); }
    }

    private JObject RecordFor(MaaProcessor processor)
    {
        if (records.TryGetValue(processor, out var record)) return record;
        record = ReadRecord(processor.InstanceConfiguration.GetValue(Setting, ""))
            ?? ImportLegacy(processor.InstanceConfiguration.GetValue(LegacySetting, ""));
        records.Add(processor, record);
        return record;
    }

    private JObject? ReadRecord(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(raw)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }
        catch (JsonException ex)
        {
            Report(ex);
            return null;
        }
    }

    private JObject ImportLegacy(string raw)
    {
        var legacy = ReadRecord(raw);
        var record = new JObject();
        if (legacy?["tasks"] is not JObject tasks) return record;
        var date = (string?)legacy["date"];
        foreach (var task in tasks.Properties())
        {
            var separator = task.Name.IndexOf('\n');
            if (separator < 0 || separator == task.Name.Length - 1 || task.Value.Type != JTokenType.String) continue;
            var key = task.Name.Substring(separator + 1);
            var time = (string)task.Value!;
            DateTimeOffset? completedAt = null;
            if (DateTime.TryParseExact(date + " " + time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var legacyTime))
                completedAt = new DateTimeOffset(legacyTime);
            else if (time.Contains("T") && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
                completedAt = timestamp;

            // Two old keys can map to the same task ID if its entry changed.
            if (record[key] is JObject existing && ReadTime(existing) is { } previous
                && (completedAt == null || previous >= completedAt.Value)) continue;
            record[key] = MakeRecord(TaskRunState.Succeeded, completedAt);
        }
        return record;
    }

    private static JObject MakeRecord(TaskRunState state, DateTimeOffset? at) => new JObject
    {
        ["state"] = state.ToString(),
        ["at"] = at?.ToString("O", CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset? ReadTime(JObject record, string field = "at") => record[field]?.Type == JTokenType.String
        && DateTimeOffset.TryParse((string?)record[field], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        ? at
        : null;

    private static DateTimeOffset? ReadSuccessTime(JObject record) => ReadTime(record, "succeeded_at")
        ?? ((string?)record["state"] == nameof(TaskRunState.Succeeded) ? ReadTime(record) : null);

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DragItemViewModel.RunState) || sender is not DragItemViewModel task
            || !tasks.TryGetValue(task, out var processor) || TaskKey(task) is not { Length: > 0 } key) return;
        var state = task.RunState;
        if (key == GuildRewardTask)
        {
            // Wait for the schedule decision before replacing the last actual run.
            if (state == TaskRunState.Running) return;
            if ((state is TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Stopped)
                && guildRewardSkips.Remove(task, out var runId) && runId == task.RunId)
            {
                if (state == TaskRunState.Succeeded) task.RunState = TaskRunState.Skipped;
                return;
            }
        }
        // Queued and Skipped describe the queue, not an actual attempt at this task.
        if (state is not (TaskRunState.Running or TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Stopped))
            return;
        if (processor.ViewModel is { } vm && HistoryKey(vm, task) is { } historyKey)
            SaveRun(processor, historyKey, state);
    }

    internal void RecordGuildRewardDecision(MaaProcessor processor, bool skip)
    {
        TrackTasks();
        var task = processor.ViewModel?.TaskItemViewModels.FirstOrDefault(item =>
            item.RunState == TaskRunState.Running && TaskKey(item) == GuildRewardTask);
        if (task == null) return;
        if (skip)
            guildRewardSkips[task] = task.RunId;
        else
        {
            guildRewardSkips.Remove(task);
            if (processor.ViewModel is { } vm && HistoryKey(vm, task) is { } key)
                SaveRun(processor, key, TaskRunState.Running);
        }
    }

    private void SaveRun(MaaProcessor processor, string key, TaskRunState state)
    {
        try
        {
            var record = RecordFor(processor);
            var now = DateTimeOffset.Now;
            var entry = MakeRecord(state, now);
            var succeededAt = state == TaskRunState.Succeeded ? now
                : record[key] is JObject previous ? ReadSuccessTime(previous) : null;
            if (succeededAt is { } success && success.ToLocalTime().Date == now.Date)
                entry["succeeded_at"] = success.ToString("O", CultureInfo.InvariantCulture);
            record[key] = entry;
            processor.InstanceConfiguration.SetValue(Setting, record.ToString(Formatting.None));
        }
        catch (Exception ex) { Report(ex); }
    }

    private void Track(DragItemViewModel task, MaaProcessor processor)
    {
        if (tasks.ContainsKey(task)) return;
        tasks.Add(task, processor);
        task.PropertyChanged += OnTaskChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // New items can run before the visual guard allows the next layout refresh.
        if (sender is not INotifyCollectionChanged collection || !collections.TryGetValue(collection, out var processor)
            || args.NewItems == null) return;
        foreach (var task in args.NewItems.OfType<DragItemViewModel>()) Track(task, processor);
    }

    private void TrackTasks()
    {
        var processors = MaaProcessor.Processors.ToArray();
        var currentTasks = new HashSet<DragItemViewModel>();
        var currentCollections = new HashSet<INotifyCollectionChanged>();
        foreach (var processor in processors)
        {
            if (processor.ViewModel == null) continue;
            var collection = processor.ViewModel.TaskItemViewModels;
            currentCollections.Add(collection);
            if (!collections.ContainsKey(collection))
            {
                collections.Add(collection, processor);
                collection.CollectionChanged += OnCollectionChanged;
            }
            foreach (var task in collection)
            {
                currentTasks.Add(task);
                Track(task, processor);
            }
        }
        foreach (var collection in collections.Keys.Where(collection => !currentCollections.Contains(collection)).ToArray())
        {
            collection.CollectionChanged -= OnCollectionChanged;
            collections.Remove(collection);
        }
        foreach (var task in tasks.Keys.Where(task => !currentTasks.Contains(task)).ToArray())
        {
            task.PropertyChanged -= OnTaskChanged;
            tasks.Remove(task);
            guildRewardSkips.Remove(task);
        }
        foreach (var processor in records.Keys.Where(processor => !processors.Contains(processor)).ToArray())
            records.Remove(processor);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        try
        {
            TrackTasks();
            if (TaskListBusy()) return;

            var shown = new HashSet<Grid>();
            if (Instances.TopLevel is { } root)
            {
                foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>().ToArray())
                {
                    if (view.DataContext is not TaskQueueViewModel vm) continue;
                    var record = RecordFor(vm.Processor);
                    bool failurePaused = (AppDomain.CurrentDomain.GetData("TKFMaa.TaskPause.IsFailurePaused")
                        as Func<MaaProcessor, bool>)?.Invoke(vm.Processor) == true;
                    var rows = view.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "ListGrid").ToArray();
                    foreach (var row in rows)
                    {
                        if (row.DataContext is not DragItemViewModel task) continue;
                        var title = row.Children.OfType<Grid>().FirstOrDefault(grid => Grid.GetColumn(grid) == 1);
                        var lastRun = HistoryKey(vm, task) is { } key ? record[key] as JObject : null;
                        bool taskFailurePaused = failurePaused && task.RunState == TaskRunState.Running;
                        var text = RowStatus(task.RunState, taskFailurePaused, lastRun);
                        var hasBadge = title != null && text != null;
                        var status = row.Children.OfType<Grid>().FirstOrDefault(grid => Grid.GetColumn(grid) == 5);
                        var spinner = status?.Children.OfType<Control>()
                            .FirstOrDefault(icon => icon.Classes.Contains("TaskRunSpinner"));
                        bool showSpinner = task.IsRunRunning && !taskFailurePaused;
                        if (spinner != null && spinner.IsVisible != showSpinner)
                            spinner.SetCurrentValue(Visual.IsVisibleProperty, showSpinner);
                        var elapsed = row.Children.OfType<TextBlock>().FirstOrDefault(block => Grid.GetColumn(block) == 2);
                        var showElapsed = !hasBadge && task.IsRunElapsedVisible;
                        if (elapsed != null && elapsed.IsVisible != showElapsed)
                            elapsed.SetCurrentValue(Visual.IsVisibleProperty, showElapsed);
                        if (title == null || text == null) continue;
                        shown.Add(title);
                        Show(title, text.Value);
                    }
                }
            }
            foreach (var title in labels.Keys.Where(title => !shown.Contains(title)).ToArray()) Hide(title);
        }
        catch (Exception ex) { Report(ex); }
    }

    private static StatusBadge? RowStatus(TaskRunState state, bool failurePaused, JObject? lastRun)
    {
        switch (state)
        {
            case TaskRunState.Running:
                return failurePaused ? new StatusBadge("故障暂停") : null;
            case TaskRunState.Queued:
                return new StatusBadge("排队中");
            case TaskRunState.Skipped:
                return new StatusBadge("本轮未执行");
            case TaskRunState.Succeeded:
            case TaskRunState.Failed:
            case TaskRunState.Stopped:
                return (string?)lastRun?["state"] == state.ToString()
                    ? HistoryText(lastRun!) : new StatusBadge(StatusText(state.ToString())!);
            default:
                return lastRun == null ? null : HistoryText(lastRun, "上次 ");
        }
    }

    private static string? StatusText(string? state) => state switch
    {
        nameof(TaskRunState.Succeeded) => "已完成",
        nameof(TaskRunState.Failed) => "失败",
        nameof(TaskRunState.Stopped) => "已停止",
        nameof(TaskRunState.Running) => "运行中断（开始时间）",
        _ => null,
    };

    private static StatusBadge? HistoryText(JObject record, string prefix = "")
    {
        var state = (string?)record["state"];
        var status = StatusText(state);
        if (status == null) return null;
        if (ReadTime(record) is not { } at) return new StatusBadge(status, Prefix: prefix);
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        var date = local.ToString(local.Year == today.Year ? "M月d日" : "yyyy年M月d日", CultureInfo.InvariantCulture);
        var timeAndStatus = local.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + status;
        if (local.Date != today) return new StatusBadge(" " + timeAndStatus, date, prefix);
        var color = state == nameof(TaskRunState.Succeeded) ? Brushes.SeaGreen : null;
        return new StatusBadge(date + " " + timeAndStatus, Prefix: prefix, Color: color);
    }

    private void Show(Grid title, StatusBadge badge)
    {
        if (!labels.TryGetValue(title, out var label))
        {
            var column = new ColumnDefinition { Width = GridLength.Auto };
            var text = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(6, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(text, title.ColumnDefinitions.Count);
            title.ColumnDefinitions.Add(column);
            title.Children.Add(text);
            label = (column, text, null);
        }
        if (label.Badge == badge) return;
        var inlines = label.Text.Inlines!;
        inlines.Clear();
        if (badge.NonTodayDate is { } date)
        {
            if (badge.Prefix.Length > 0) inlines.Add(new Run(badge.Prefix));
            inlines.Add(new Run(date) { Foreground = Brushes.IndianRed });
            inlines.Add(new Run(badge.Text));
        }
        else
        {
            var run = new Run(badge.Prefix + badge.Text);
            if (badge.Color != null) run.Foreground = badge.Color;
            inlines.Add(run);
        }
        labels[title] = (label.Column, label.Text, badge);
    }

    private void Hide(Grid title)
    {
        var (column, text, _) = labels[title];
        title.Children.Remove(text);
        title.ColumnDefinitions.Remove(column);
        labels.Remove(title);
    }

    private void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        LoggerHelper.Error("TKFMaa 任务运行记录：" + ex.Message);
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.SetData(CanContinueKey, null);
        AppDomain.CurrentDomain.SetData(ContinueKey, null);
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var collection in collections.Keys) collection.CollectionChanged -= OnCollectionChanged;
        collections.Clear();
        foreach (var task in tasks.Keys) task.PropertyChanged -= OnTaskChanged;
        tasks.Clear();
        guildRewardSkips.Clear();
        foreach (var title in labels.Keys.ToArray()) Hide(title);
        records.Clear();
    }
}
