using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MaaFramework.Binding.Buffers;
using Markdown.Avalonia.Full;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Each source file is compiled separately by MFA. Keep the scheduler and its action together.
public sealed class TkfmDispatchSchedule : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDispatchSchedule";
    private const string ServiceKey = "TKFMaa.Dispatch.Schedule.Service";
    private static readonly object ServiceLock = new object();

    public TkfmDispatchSchedule() { Service(); }

    private static DispatchClock Service()
    {
        lock (ServiceLock)
        {
            var clock = AppDomain.CurrentDomain.GetData(ServiceKey) as DispatchClock;
            if (clock != null) return clock;
            (AppDomain.CurrentDomain.GetData(ServiceKey) as IDisposable)?.Dispose();
            clock = new DispatchClock();
            AppDomain.CurrentDomain.SetData(ServiceKey, clock);
            return clock;
        }
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        MaaProcessor processor = null;
        DispatchClock clock = null;
        JObject job = null;
        bool timed = false;
        try
        {
            processor = MaaProcessor.Processors.ToArray()
                .FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
            if (processor == null) return false;

            clock = Service();
            var mode = ParseObject(args.ActionParam, "动作参数");
            if ((string)mode["mode"] == "countdown")
                return CaptureCountdown(context, processor, clock, (string)mode["source"] == "drawer");
            clock.ClearCountdown(processor);

            var existing = clock.Read(processor);
            timed = (string)existing?["state"] == "queued";
            JObject overrides;
            if (timed)
            {
                job = existing;
                clock.Change(processor, (string)job["id"], j => j["state"] = "running");
                overrides = (JObject)job["overrides"].DeepClone();
                var login = LoginOverrides(processor);
                processor.AddLog("[定时派遣] 到点执行：重新启动游戏，处理已出现的遮挡，识别到无遮挡出征后进入派遣；不等待广告。", changeColor: false);
                if (!Succeeded(context, "TkfmRecoveryRestartGame", login))
                    throw new InvalidOperationException("游戏启动或登录失败，保留现场，不自动重复预约");
            }
            else
            {
                overrides = new JObject();
            }
            // The shared main-page exit uses StopTask for top-level tasks. Inside this
            // wrapper it must end the child naturally so success cleanup can still run.
            overrides["CommonStopOnMain"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
            };
            bool success = Succeeded(context, "DailyDispatchRunNow", overrides);
            bool completed = success && !context.IsCancellationRequested;
            var countdown = clock.TakeCountdown(processor);
            if (timed)
                clock.Change(processor, (string)job["id"], j =>
                {
                    j["state"] = completed ? "succeeded" : "failed";
                    j["result"] = completed ? "派遣执行完成" : "派遣失败或被停止，保留现场";
                    if (completed && countdown != null) j["next_due"] = countdown;
                });
            else if (completed && countdown != null)
            {
                try { clock.SaveCountdown(processor, new JObject(), new JObject(), countdown, deferUntilCleanup: true); }
                catch (Exception ex) { processor.AddLog("[自动最速派遣] 未能保存下次派遣时间：" + ex.Message, "OrangeRed"); }
            }
            return completed;
        }
        catch (Exception ex)
        {
            if (processor != null)
                processor.AddLog("[定时派遣] " + ex.Message, "OrangeRed");
            else
                LoggerHelper.Error("TKFMaa 定时派遣：" + ex);
            if (timed && job != null && clock != null)
            {
                try
                {
                    clock.Change(processor, (string)job["id"], j =>
                    {
                        j["state"] = "failed";
                        j["result"] = ex.Message;
                    });
                }
                catch (Exception updateError)
                {
                    LoggerHelper.Error("TKFMaa 定时派遣状态更新失败：" + updateError.Message);
                }
            }
            return false;
        }
    }

    private static JObject ParseObject(string raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
            return new JObject();
        try
        {
            return JObject.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(label + "不是有效 JSON 对象：" + ex.Message, ex);
        }
    }

    private static bool CaptureCountdown(IMaaContext context, MaaProcessor processor, DispatchClock clock, bool fromDrawer)
    {
        clock.ClearCountdown(processor);
        if (context.IsCancellationRequested) return false;
        string countdownNode = fromDrawer ? "DailyDispatchDrawerCountdownSlot" : "DailyDispatchCountdownSlot";
        try
        {
            using (var image = new MaaImageBuffer())
            {
                // The all-busy recognition just captured this frame. Read all slots from
                // that same normalized device image before navigation leaves the page.
                if (!context.Tasker.Controller.GetCachedImage(image))
                    throw new InvalidOperationException("无法取得派遣截图");
                var observedAt = DateTimeOffset.Now;
                var seconds = new List<int>();
                for (int slot = 1; slot <= 3; slot++)
                {
                    if (context.IsCancellationRequested) return false;
                    using (var result = context.RunRecognition(countdownNode + slot, image))
                    {
                        var detail = result == null ? new JObject() : JObject.Parse(result.Detail);
                        var candidates = new List<string>();
                        if (detail["best"]?["text"] != null) candidates.Add((string)detail["best"]["text"]);
                        else if (detail["filtered"] is JArray filtered)
                            candidates.AddRange(filtered.OfType<JObject>().Select(item => (string)item["text"] ?? ""));
                        processor.AddLog("[智能派遣] 第" + slot + "队倒计时 OCR："
                            + (candidates.Count == 0 ? "未读到" : string.Join(" / ", candidates)), changeColor: false);
                        var values = new HashSet<int>();
                        foreach (string text in candidates)
                        {
                            var match = Regex.Match(Regex.Replace(text.Replace('：', ':'), @"\s+", ""),
                                @"^(\d{2}):([0-5]\d):([0-5]\d)$");
                            if (match.Success) values.Add(int.Parse(match.Groups[1].Value) * 3600
                                + int.Parse(match.Groups[2].Value) * 60 + int.Parse(match.Groups[3].Value));
                        }
                        seconds.Add(result?.Hit == true && values.Count == 1 ? values.Single() : -1);
                    }
                }
                if (seconds.Any(value => value < 0 || (fromDrawer && value == 0)))
                    throw new InvalidOperationException("三队倒计时未全部完整识别");
                int longest = seconds.Max();
                var due = observedAt.AddSeconds(longest + 5);
                clock.RecordCountdown(processor, due.ToString("O"));
                if (fromDrawer)
                    processor.AddLog("[智能派遣] 左侧三队均在派遣中，本轮已完成。", changeColor: false);
            }
        }
        catch (Exception ex)
        {
            processor.AddLog("[智能派遣] 未能计算下次派遣时间：" + ex.Message, "OrangeRed");
            if (fromDrawer) return false;
        }
        return !context.IsCancellationRequested;
    }

    private static bool Succeeded(IMaaContext context, string entry, JObject overrides) =>
        !context.IsCancellationRequested
        && context.RunTask(entry, overrides.ToString(Formatting.None))?.Status == MaaJobStatus.Succeeded
        && !context.IsCancellationRequested;

    private static JObject LoginOverrides(MaaProcessor processor)
    {
        string resource = processor.ViewModel?.CurrentResource;
        string package = resource == "官服" ? "com.pinkcore.tkfm"
            : resource == "erolab服务器" ? "com.pinkcore.tkfm.erolabs"
            : throw new InvalidOperationException("资源类型对应的服务器配置无效：" + resource);
        var overrides = new JObject();
        overrides["TkfmRecoveryRestartGame"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "StopApp", ["param"] = new JObject { ["package"] = package } }
        };
        overrides["GameLoginLaunch"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "StartApp", ["param"] = new JObject { ["package"] = package } }
        };
        overrides["GameMainReady"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
        };
        return overrides;
    }
}

internal sealed class DispatchClock : IDisposable
{
    private static bool TaskListBusy() =>
        (AppDomain.CurrentDomain.GetData("TKFMaa.TaskListGuard.IsBusy") as Func<bool>)?.Invoke() == true;

    private const string Setting = "TkfmDispatchReservation";
    private readonly object sync = new object();
    private readonly Dictionary<string, string> countdowns = new Dictionary<string, string>();

    internal void ClearCountdown(MaaProcessor processor) { lock (sync) countdowns.Remove(processor.InstanceId); }
    internal void RecordCountdown(MaaProcessor processor, string due) { lock (sync) countdowns[processor.InstanceId] = due; }
    internal string TakeCountdown(MaaProcessor processor)
    {
        lock (sync)
        {
            countdowns.TryGetValue(processor.InstanceId, out var due);
            countdowns.Remove(processor.InstanceId);
            return due;
        }
    }

    internal void SaveCountdown(MaaProcessor processor, JObject config, JObject overrides, string due,
        bool deferUntilCleanup = false, string expectedJobId = null)
    {
        ArmAt(processor, config, overrides, DateTimeOffset.Parse(due), true, deferUntilCleanup, expectedJobId);
    }
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> working = new HashSet<string>();
    private readonly Dictionary<string, string> busyLogged = new Dictionary<string, string>();
    private readonly Dictionary<string, CancellationTokenSource> operations = new Dictionary<string, CancellationTokenSource>();
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private bool disposed;
    private const string AutoStart = "定时任务允许自动启动模拟器";
    private const string AutoClose = "定时任务执行完毕后关闭模拟器";
    private const string Dispatch = "自动最速派遣";
    private const string DispatchEntry = "DailyDispatchStart";
    private const string Daily = "定时执行所有日常任务";
    private const string DailyPolicy = "TkfmDailyQueuePolicy";
    private readonly Dictionary<string, DateTime> pendingDaily = new Dictionary<string, DateTime>();
    private readonly Dictionary<string, Action> dailyRuns = new Dictionary<string, Action>();
    private readonly HashSet<string> migratedTimers = new HashSet<string>();
    private readonly HashSet<string> invalidTimes = new HashSet<string>();
    private readonly HashSet<StackPanel> testPanels = new HashSet<StackPanel>();
    private readonly Dictionary<ToggleSwitch, EventHandler<RoutedEventArgs>> dispatchToggles = new();
    private readonly Dictionary<Grid, (DockPanel Host, TextBlock Title, Border Clock)> dispatchClocks = new();
    private readonly HashSet<Button> dispatchCancelButtons = new();
    private readonly Dictionary<ComboBox, (string Text, IDisposable Placeholder, IDisposable Tip)> controllerLabels = new();
    private const string DispatchTimePrefix = "下次派遣时间：";
    private static readonly Regex DispatchStatusLine = new Regex($"(?m)^{DispatchTimePrefix}[^\r\n]*");
    private readonly HashSet<DragItemViewModel> startupTasks = new HashSet<DragItemViewModel>();
    private readonly Dictionary<CheckBox, (IDisposable Enabled, IDisposable Template)> startupCheckboxes = new();
    private static readonly FuncControlTemplate<CheckBox> LockedStartupTemplate = new((_, _) => new Border
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(3),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromRgb(112, 112, 112)),
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 1,5 L 4,8 L 10,2"),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    });

    internal static bool AutoDispatchEnabled(MaaProcessor processor) => Enabled(processor, Dispatch);

    private static DragItemViewModel DispatchTask(MaaProcessor processor) =>
        processor.ViewModel?.TaskItemViewModels.FirstOrDefault(row => row.InterfaceItem?.Entry == DispatchEntry);

    private static MaaInterface.MaaInterfaceSelectOption Option(MaaProcessor processor, string name)
    {
        var task = name == Dispatch ? DispatchTask(processor) : processor.ViewModel?.TaskItemViewModels
            .FirstOrDefault(row => row.InterfaceItem?.Entry == "GameLoginStart");
        return task?.InterfaceItem?.Option?.FirstOrDefault(option => option.Name == name);
    }

    private static bool Enabled(MaaProcessor processor, string name) => Option(processor, name)?.Index == 0;

    private static string ReadTime(MaaProcessor processor, string option, string fallback)
    {
        var data = Option(processor, option)?.SubOptions?.FirstOrDefault()?.Data;
        return data != null && data.TryGetValue("时间", out var time) ? time?.Trim() ?? "" : fallback;
    }

    internal DispatchClock()
    {
        AppDomain.CurrentDomain.SetData("TKFMaa.Dispatch.Schedule.IsBusy",
            (Func<bool>)(() => !disposed && (working.Count > 0 || dailyRuns.Count > 0)));
        Dispatcher.UIThread.Post(() => { if (!disposed) { timer.Tick += Tick; timer.Start(); } });
    }

    internal JObject Read(MaaProcessor processor)
    {
        lock (sync)
        {
            string raw = processor.InstanceConfiguration.GetValue(Setting, "");
            return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
        }
    }

    private void Save(MaaProcessor processor, JObject job) =>
        processor.InstanceConfiguration.SetValue(Setting, job.ToString(Formatting.None));

    internal void Change(MaaProcessor processor, string id, Action<JObject> change)
    {
        lock (sync)
        {
            var current = Read(processor);
            if (current == null || (string)current["id"] != id) return;
            change(current);
            Save(processor, current);
        }
    }

    private void ArmAt(MaaProcessor processor, JObject config, JObject overrides, DateTimeOffset due, bool countdown,
        bool deferUntilCleanup = false, string expectedJobId = null)
    {
        bool calculatedOnly = countdown && !AutoDispatchEnabled(processor);
        var job = new JObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["session"] = session,
            ["state"] = calculatedOnly ? "calculated" : "pending",
            ["due"] = due.ToString("O"), ["from_countdown"] = countdown,
            ["config"] = config.DeepClone(), ["overrides"] = overrides.DeepClone(),
            ["handled"] = calculatedOnly
        };
        if (!calculatedOnly) job.Merge(DispatchDevice.Snapshot(processor));
        bool deferred = false;
        lock (sync)
        {
            var old = Read(processor);
            if (expectedJobId != null && (string)old?["id"] != expectedJobId) return;
            string state = (string)old?["state"];
            if (state is "launching" or "queued" or "running")
                throw new InvalidOperationException("已有定时派遣正在启动或执行，请先停止当前任务");
            if ((state is "succeeded" or "failed" or "cancelled") && (bool?)old["handled"] != true)
            {
                if (!deferUntilCleanup)
                    throw new InvalidOperationException("上一轮正在收尾，请稍后重新预约");
                old["next_reservation"] = job;
                Save(processor, old);
                deferred = true;
            }
            else
            {
                if ((bool?)old?["policy_saved"] == true)
                    throw new InvalidOperationException("上一轮正在收尾，请稍后重新预约");
                Save(processor, job);
            }
        }
        if (deferred)
        {
            string time = due.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            processor.AddLog("[智能派遣] 已保存下次派遣时间 " + time + "，上一轮收尾后生效。", changeColor: false);
        }
        else LogReservation(processor, job);
    }

    private static void LogReservation(MaaProcessor processor, JObject job)
    {
        string time = DateTimeOffset.Parse((string)job["due"]).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if ((string)job["state"] == "calculated")
        {
            processor.AddLog("[智能派遣] 已计算下次派遣时间 " + time + "。开启自动最速派遣后才会执行。", changeColor: false);
            return;
        }
        processor.AddLog(((bool?)job["from_countdown"] == true ? "[智能派遣] 已根据最长倒计时预约 " : "[自动最速派遣] 已登记首次派遣 ")
            + time + "（电脑本地时间）。助手须保持运行，可在「每日派遣」设置中取消自动派遣。", changeColor: false);
    }

    private static bool Busy(MaaProcessor processor) =>
        (bool?)processor.GetType().GetProperty("IsTaskRunActive")?.GetValue(processor) == true
        || processor.ViewModel?.IsRunning == true || processor.ViewModel?.ToggleEnable == false
        || processor.MaaTasker?.IsRunning == true;

    private bool SharedBusy(MaaProcessor owner, JObject job) => MaaProcessor.Processors.ToArray().Any(p =>
        !ReferenceEquals(p, owner) && (Busy(p) || working.Contains(p.InstanceId))
        && p.Config.AdbDevice.AdbSerial == (string)job["serial"]);

    private void Tick(object sender, EventArgs args)
    {
        if (disposed) return;
        var processors = MaaProcessor.Processors.ToArray();
        if (!TaskListBusy()) LockStartupSelection(processors);
        foreach (var processor in processors)
        {
            try
            {
                if (!Busy(processor)) MigrateDailyTimers(processor);
                if (dailyRuns.TryGetValue(processor.InstanceId, out var finishDaily) && !Busy(processor))
                {
                    finishDaily();
                    continue;
                }
                if (!working.Contains(processor.InstanceId) && !Busy(processor)) RestoreDailyPolicy(processor);
                MaybeSchedule(processor);
                var job = Read(processor);
                if (job == null) continue;
                string id = (string)job["id"];
                if ((string)job["session"] != session)
                {
                    string state = (string)job["state"];
                    if (state == "pending" && DateTimeOffset.Parse((string)job["due"]) <= DateTimeOffset.Now)
                    {
                        Change(processor, id, j => { j["state"] = "missed"; j["handled"] = true; });
                        processor.AddLog("[定时派遣] 预约时间已在助手离线期间错过，已失效；请重新预约，不会补跑。", "OrangeRed");
                    }
                    else if (new[] { "launching", "queued", "running" }.Contains(state))
                    {
                        Change(processor, id, j => { j["state"] = "failed"; j["result"] = "上次执行被中断，不自动重试"; });
                        processor.AddLog("[定时派遣] 上次执行被中断，已停止预约，请检查派遣状态。", "OrangeRed");
                    }
                    Change(processor, id, j => j["session"] = session);
                    job = Read(processor);
                }
                if (working.Contains(processor.InstanceId)) continue;
                if ((string)job["state"] == "pending")
                {
                    if (!Enabled(processor, Dispatch))
                    {
                        Change(processor, id, j =>
                        {
                            j["state"] = (bool?)j["from_countdown"] == true ? "calculated" : "cancelled";
                            j["handled"] = true;
                        });
                        continue;
                    }
                    if (DateTimeOffset.Parse((string)job["due"]) > DateTimeOffset.Now) continue;
                    if (Busy(processor) || SharedBusy(processor, job))
                    {
                        if (!busyLogged.TryGetValue(processor.InstanceId, out var last) || last != id)
                        {
                            busyLogged[processor.InstanceId] = id;
                            processor.AddLog("[定时派遣] 已到预约时间，等待当前设备任务空闲。", changeColor: false);
                        }
                        continue;
                    }
                    working.Add(processor.InstanceId);
                    _ = Launch(processor, job);
                }
                else if (new[] { "queued", "running" }.Contains((string)job["state"]) && !Busy(processor))
                {
                    Change(processor, id, j => { j["state"] = "failed"; j["result"] = "连接失败或队列被停止，未完成派遣"; });
                    processor.AddLog("[定时派遣] 连接失败或队列被停止，预约已结束，保留现场。", "OrangeRed");
                }
                else if ((bool?)job["handled"] != true && new[] { "succeeded", "failed", "cancelled" }.Contains((string)job["state"]) && !Busy(processor))
                {
                    working.Add(processor.InstanceId);
                    _ = Finish(processor, job);
                }
            }
            catch (Exception ex) { LoggerHelper.Error("TKFMaa 派遣调度：" + ex.Message); }
        }
        RefreshControllerLabels();
        if (TaskListBusy()) return;
        RefreshDispatchUi();
        InstallLaunchTest();
    }

    private void LockStartupSelection(IEnumerable<MaaProcessor> processors)
    {
        var currentTasks = processors.Where(processor => processor.ViewModel != null)
            .SelectMany(processor => processor.ViewModel.TaskItemViewModels)
            .Where(task => task.InterfaceItem?.Entry == "GameLoginStart").ToHashSet();
        foreach (var task in startupTasks.Where(task => !currentTasks.Contains(task)).ToArray())
        {
            task.PropertyChanged -= KeepStartupChecked;
            startupTasks.Remove(task);
        }
        foreach (var task in currentTasks)
        {
            if (startupTasks.Add(task)) task.PropertyChanged += KeepStartupChecked;
            if (task.IsCheckedWithNull != true) task.IsChecked = true;
        }

        var currentCheckboxes = Instances.TopLevel?.GetVisualDescendants().OfType<TaskQueueView>()
            .SelectMany(view => view.GetVisualDescendants().OfType<CheckBox>())
            .Where(box => box.DataContext is DragItemViewModel task && startupTasks.Contains(task))
            .ToHashSet() ?? new HashSet<CheckBox>();
        foreach (var box in startupCheckboxes.Keys.Where(box => !currentCheckboxes.Contains(box)).ToArray())
            ReleaseStartupCheckbox(box);
        foreach (var box in currentCheckboxes)
        {
            if (startupCheckboxes.ContainsKey(box)) continue;
            var enabled = box.SetValue(Control.IsEnabledProperty, false, BindingPriority.Animation);
            var template = box.SetValue(TemplatedControl.TemplateProperty, LockedStartupTemplate, BindingPriority.Animation);
            startupCheckboxes.Add(box, (enabled, template));
        }
    }

    private void ReleaseStartupCheckbox(CheckBox box)
    {
        var (enabled, template) = startupCheckboxes[box];
        template?.Dispose();
        enabled?.Dispose();
        startupCheckboxes.Remove(box);
    }

    private static void KeepStartupChecked(object sender, PropertyChangedEventArgs args)
    {
        // Select None changes the row model directly, without clicking its checkbox.
        if (args.PropertyName == nameof(DragItemViewModel.IsCheckedWithNull)
            && sender is DragItemViewModel task && task.IsCheckedWithNull != true)
            task.IsChecked = true;
    }

    private void MigrateDailyTimers(MaaProcessor processor)
    {
        const string key = "TkfmSchedulePanelV1";
        var daily = Option(processor, Daily);
        if (daily == null || !migratedTimers.Add(processor.InstanceId)
            || processor.InstanceConfiguration.GetValue(key, false)) return;
        var timers = TimerModel.Instance.Timers.Where(t => t.IsOn && t.TimerConfig == processor.InstanceId
            && t.TimerAction == TimerActionType.StartTask && t.ScheduleConfig.ScheduleType == TimerScheduleType.Daily).ToArray();
        if (timers.Length > 0)
        {
            daily.Index = 0;
            daily.SubOptions = new List<MaaInterface.MaaInterfaceSelectOption>
            {
                new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = "日常执行时间", Index = 0, Data = new Dictionary<string, string>
                    {
                        ["时间"] = string.Join(", ", timers.Select(t => t.Time.ToString(@"hh\:mm")).Distinct().OrderBy(time => time))
                    }
                }
            };
            var last = timers.Where(timer => timer.LastTriggered.HasValue).Select(timer => timer.LastTriggered.Value).DefaultIfEmpty().Max();
            processor.InstanceConfiguration.SetValue("TkfmLastDailyMinute", last.ToString("yyyyMMddHHmm"));
            processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
                processor.ViewModel.TaskItemViewModels.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
            foreach (var timer in timers) timer.IsOn = false;
        }
        processor.InstanceConfiguration.SetValue(key, true);
    }

    private void MaybeSchedule(MaaProcessor processor)
    {
        var now = DateTime.Now;
        string minute = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        string hhmm = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        string id = processor.InstanceId;
        if (!Enabled(processor, Daily))
        {
            pendingDaily.Remove(id);
            return;
        }
        string[] times = ReadTime(processor, Daily, "17:30").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(time => time.Trim()).ToArray();
        if (times.Length == 0 || times.Any(time => !DateTime.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            if (invalidTimes.Add(processor.InstanceId)) processor.AddLog("[定时日常] 执行时间格式须为 HH:mm。", "OrangeRed");
            return;
        }
        invalidTimes.Remove(processor.InstanceId);
        const string dailyKey = "TkfmLastDailyMinute";
        if (times.Contains(hhmm) && processor.InstanceConfiguration.GetValue(dailyKey, "") != minute)
        {
            processor.InstanceConfiguration.SetValue(dailyKey, minute);
            pendingDaily[id] = now.Date;
        }
        if (!pendingDaily.TryGetValue(id, out DateTime triggered)) return;
        if (triggered != now.Date) { pendingDaily.Remove(id); return; }
        if (working.Contains(id) || Busy(processor) || processor.IsConnecting) return;
        var dispatch = Read(processor);
        if ((string)dispatch?["state"] == "pending" && Enabled(processor, Dispatch)
            && DateTimeOffset.Parse((string)dispatch["due"]) <= DateTimeOffset.Now) return;
        pendingDaily.Remove(id);
        var device = DispatchDevice.Snapshot(processor);
        if (SharedBusy(processor, device)) { pendingDaily[id] = triggered; return; }
        working.Add(id);
        _ = LaunchDaily(processor, device);
    }

    private async Task LaunchDaily(MaaProcessor processor, JObject device)
    {
        const string label = "[定时日常] ";
        using var cancellation = new CancellationTokenSource();
        operations[processor.InstanceId] = cancellation;
        bool queued = false;
        try
        {
            await PrepareDevice(processor, device, cancellation.Token, label);
            if (disposed || processor.IsClosed || !Enabled(processor, Daily)) return;
            if (Busy(processor) || SharedBusy(processor, device))
                throw new InvalidOperationException("设备正在执行其他任务");
            DispatchDevice.RebindSavedDevice(processor, device);
            var tasks = processor.ViewModel.TaskItemViewModels.Where(task => !task.IsResourceOptionItem
                && (task.IsChecked || !task.IsCheckedWithNull.HasValue)).ToList();
            if (tasks.Count == 0) throw new InvalidOperationException("当前配置没有勾选任务");
            var baseline = tasks.Select(task => task.RunId).ToArray();
            processor.SetTasker();
            SuspendDailyPolicy(processor);
            try
            {
                processor.AddLog(label + "开始执行当前勾选的日常任务。", changeColor: false);
                processor.Start(tasks);
                dailyRuns[processor.InstanceId] = () =>
                {
                    dailyRuns.Remove(processor.InstanceId);
                    RestoreDailyPolicy(processor);
                    bool succeeded = tasks.Select((task, index) => task.RunId != baseline[index]
                        && task.RunState.ToString() == "Succeeded").All(value => value);
                    _ = FinishDaily(processor, device, succeeded);
                };
                queued = true;
            }
            catch { RestoreDailyPolicy(processor); throw; }
        }
        catch (Exception ex) { if (!disposed) processor.AddLog(label + ex.Message, "OrangeRed"); }
        finally
        {
            operations.Remove(processor.InstanceId);
            if (!queued) working.Remove(processor.InstanceId);
        }
    }

    private async Task FinishDaily(MaaProcessor processor, JObject device, bool succeeded)
    {
        try
        {
            if (!succeeded)
                processor.AddLog("[定时日常] 队列未全部成功，保留模拟器现场。", changeColor: false);
            if (succeeded && Enabled(processor, AutoClose) && !SharedBusy(processor, device))
                await CloseEmulator(processor, device, "[定时日常] ");
        }
        catch (Exception ex) { processor.AddLog("[定时日常] 模拟器关闭失败：" + ex.Message, "OrangeRed"); }
        finally { working.Remove(processor.InstanceId); }
    }

    private static void SuspendDailyPolicy(MaaProcessor processor)
    {
        var config = processor.InstanceConfiguration;
        var saved = new JObject
        {
            ["retry"] = config.GetValue(ConfigurationKeys.RetryOnDisconnected, false),
            ["detect"] = config.GetValue("AutoDetectOnConnectionFailed", true),
            ["before"] = config.GetValue("BeforeTask", "None"),
            ["after"] = config.GetValue("AfterTask", "None")
        };
        config.SetValue(DailyPolicy, saved.ToString(Formatting.None));
        config.SetValue(ConfigurationKeys.RetryOnDisconnected, false);
        config.SetValue("AutoDetectOnConnectionFailed", false);
        config.SetValue("BeforeTask", "None");
        config.SetValue("AfterTask", "None");
    }

    private static void RestoreDailyPolicy(MaaProcessor processor)
    {
        var config = processor.InstanceConfiguration;
        string raw = config.GetValue(DailyPolicy, "");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var saved = JObject.Parse(raw);
        config.SetValue(ConfigurationKeys.RetryOnDisconnected, (bool)saved["retry"]);
        config.SetValue("AutoDetectOnConnectionFailed", (bool)saved["detect"]);
        config.SetValue("BeforeTask", (string)saved["before"]);
        config.SetValue("AfterTask", (string)saved["after"]);
        config.RemoveValue(DailyPolicy);
    }

    private async Task PrepareDevice(MaaProcessor processor, JObject device, CancellationToken token, string label)
    {
        await DispatchDevice.Prepare(processor, device, Enabled(processor, AutoStart), token, label);
    }

    private void RefreshDispatchUi()
    {
        var views = Instances.TopLevel?.GetVisualDescendants().OfType<TaskQueueView>().ToArray()
            ?? Array.Empty<TaskQueueView>();
        var shownClocks = new HashSet<Grid>();
        var shownButtons = new HashSet<Button>();
        var shownToggles = new HashSet<ToggleSwitch>();
        foreach (var view in views)
        {
            if (view.DataContext is not TaskQueueViewModel vm) continue;
            var processor = vm.Processor;
            var task = DispatchTask(processor);
            bool enabled = AutoDispatchEnabled(processor);
            if (task == null) continue;

            if (enabled)
            {
                foreach (var row in view.GetVisualDescendants().OfType<Grid>()
                    .Where(grid => grid.Name == "ListGrid" && ReferenceEquals(grid.DataContext, task)).ToArray())
                {
                    var titleGrid = row.Children.OfType<Grid>().FirstOrDefault(grid => Grid.GetColumn(grid) == 1);
                    if (titleGrid != null && ShowDispatchClock(titleGrid, NextDispatchText(processor)))
                        shownClocks.Add(titleGrid);
                }
            }

            var options = view.FindControl<StackPanel>("CommonOptionSettings");
            var toggle = task.EnableSetting ? options?.GetVisualDescendants().OfType<ToggleSwitch>()
                .FirstOrDefault(item => item.Tag as string == Dispatch) : null;
            if (toggle != null)
            {
                shownToggles.Add(toggle);
                if (!dispatchToggles.ContainsKey(toggle))
                {
                    EventHandler<RoutedEventArgs> changed = (_, __) =>
                    {
                        if (disposed || !ReferenceEquals(view.DataContext, vm) || !task.EnableSetting) return;
                        if (toggle.IsChecked == true) QueueFirstDispatch(processor);
                        else CancelAutomaticDispatch(processor);
                        RefreshDispatchUi();
                    };
                    toggle.IsCheckedChanged += changed;
                    dispatchToggles.Add(toggle, changed);
                }
                if (enabled)
                {
                    var button = options.Children.OfType<Button>().FirstOrDefault(dispatchCancelButtons.Contains);
                    if (button == null)
                    {
                        button = new Button
                        {
                            Content = "点击能够取消掉每日派遣",
                            Margin = new Thickness(0, 6, 0, 4),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        button.Click += (_, __) =>
                        {
                            if (!disposed && ReferenceEquals(view.DataContext, vm) && task.EnableSetting)
                                toggle.IsChecked = false; // Reuse native option storage and the switch handler.
                        };
                        options.Children.Add(button);
                        dispatchCancelButtons.Add(button);
                    }
                    shownButtons.Add(button);
                }
            }
            RefreshDispatchIntroduction(view, processor);
        }
        foreach (var grid in dispatchClocks.Keys.Where(grid => !shownClocks.Contains(grid)).ToArray())
            HideDispatchClock(grid);
        foreach (var button in dispatchCancelButtons.Where(button => !shownButtons.Contains(button)).ToArray())
            RemoveDispatchCancelButton(button);
        foreach (var toggle in dispatchToggles.Keys.Where(toggle => !shownToggles.Contains(toggle)).ToArray())
            UnbindDispatchToggle(toggle);
    }

    private void CancelAutomaticDispatch(MaaProcessor processor)
    {
        var job = Read(processor);
        string state = (string)job?["state"];
        if (state is "pending" or "launching")
        {
            Change(processor, (string)job["id"], current =>
            {
                current["state"] = state == "pending" && (bool?)current["from_countdown"] == true
                    ? "calculated" : "cancelled";
                current["handled"] = true;
            });
            if (state == "launching" && operations.TryGetValue(processor.InstanceId, out var operation))
                operation.Cancel();
        }
        processor.AddLog("[定时派遣] 已关闭自动最速派遣。", changeColor: false);
    }

    private bool ShowDispatchClock(Grid grid, string tooltip)
    {
        if (!dispatchClocks.TryGetValue(grid, out var decoration))
        {
            var title = grid.Children.OfType<TextBlock>().FirstOrDefault(text => Grid.GetColumn(text) == 1);
            if (title == null) return false;
            var clockFace = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 6,1 A 5,5 0 1 1 6,11 A 5,5 0 1 1 6,1 M 6,3 L 6,6 L 8,7"),
                StrokeThickness = 1.2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            clockFace.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
                new Binding(nameof(TextBlock.Foreground)) { Source = title });
            var clock = new Border
            {
                Background = Brushes.Transparent, Child = clockFace,
                Width = 16, Height = 16, Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var host = new DockPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                LastChildFill = true
            };
            DockPanel.SetDock(clock, Dock.Right);
            Grid.SetColumn(host, 1);
            grid.Children.Remove(title);
            host.Children.Add(clock);
            host.Children.Add(title);
            grid.Children.Add(host);
            decoration = (host, title, clock);
            dispatchClocks.Add(grid, decoration);
        }
        if (!Equals(ToolTip.GetTip(decoration.Clock), tooltip)) ToolTip.SetTip(decoration.Clock, tooltip);
        return true;
    }

    private void HideDispatchClock(Grid grid)
    {
        var (host, title, _) = dispatchClocks[grid];
        host.Children.Remove(title);
        grid.Children.Remove(host);
        grid.Children.Add(title);
        dispatchClocks.Remove(grid);
    }

    private void RemoveDispatchCancelButton(Button button)
    {
        if (button.Parent is Panel parent) parent.Children.Remove(button);
        dispatchCancelButtons.Remove(button);
    }

    private void UnbindDispatchToggle(ToggleSwitch toggle)
    {
        toggle.IsCheckedChanged -= dispatchToggles[toggle];
        dispatchToggles.Remove(toggle);
    }

    private void RefreshDispatchIntroduction(TaskQueueView view, MaaProcessor processor)
    {
        // The native view restores the selected task's Markdown on selection or resource reload.
        var introduction = view.FindControl<MarkdownScrollViewer>("Introduction");
        string markdown = introduction?.Markdown;
        if (string.IsNullOrEmpty(markdown)) return;
        var line = DispatchStatusLine.Match(markdown);
        if (!line.Success) return;
        string next = NextDispatchText(processor);
        if (line.Value != next)
            introduction.Markdown = markdown.Substring(0, line.Index) + next + markdown.Substring(line.Index + line.Length);
    }

    private void QueueFirstDispatch(MaaProcessor processor)
    {
        if (!AutoDispatchEnabled(processor)) return;
        try
        {
            var job = Read(processor);
            var state = (string)job?["state"];
            if (state is "pending" or "launching" or "queued" or "running") return;
            if (state == "calculated" && DateTimeOffset.TryParse((string)job["due"], out var due) && due > DateTimeOffset.Now)
            {
                ArmAt(processor, (JObject)job["config"], (JObject)job["overrides"], due, true);
                return;
            }
            ArmAt(processor, new JObject(), new JObject(), DateTimeOffset.Now, false);
        }
        catch (Exception ex) { processor.AddLog("[自动最速派遣] " + ex.Message, "OrangeRed"); }
    }

    private string NextDispatchText(MaaProcessor processor)
    {
        var job = Read(processor);
        var state = (string)job?["state"];
        bool enabled = AutoDispatchEnabled(processor);
        if (state is "launching" or "queued" or "running") return DispatchTimePrefix + "本轮派遣中，待计算";
        if ((state is "pending" or "calculated") && DateTimeOffset.TryParse((string)job["due"], out var due))
        {
            string time = due.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (!enabled) return DispatchTimePrefix + time + "（自动派遣未开启）";
            if (state == "calculated") return DispatchTimePrefix + time + "（尚未预约）";
            return DispatchTimePrefix + time + (due <= DateTimeOffset.Now ? "（已到点，等待执行）" : "");
        }
        return DispatchTimePrefix + (enabled ? "未预约，执行一次每日派遣后自动计算" : "自动派遣未开启");
    }

    private void InstallLaunchTest()
    {
        var root = Instances.TopLevel;
        if (root == null) return;
        testPanels.RemoveWhere(panel => panel.GetVisualRoot() == null);
        foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>())
        {
            if (view.DataContext is not TaskQueueViewModel vm) continue;
            foreach (var toggle in view.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.Tag as string == AutoStart).ToArray())
            {
                if (toggle.Parent is not Grid row || row.Parent is not StackPanel parent) continue;
                var panel = parent.Children.OfType<StackPanel>().FirstOrDefault(p => p.Name == "TkfmAutoStartTest");
                if (panel == null || !testPanels.Contains(panel))
                {
                    if (panel != null) parent.Children.Remove(panel);
                    panel = new StackPanel { Name = "TkfmAutoStartTest", Spacing = 4, Margin = new Thickness(0, 0, 10, 6) };
                    var button = new Button { Content = "测试自动启动", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
                    var status = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap };
                    panel.Children.Add(button);
                    panel.Children.Add(status);
                    parent.Children.Insert(parent.Children.IndexOf(row) + 1, panel);
                    testPanels.Add(panel);
                    button.Click += async (_, __) => await TestLaunch(vm.Processor, button, status);
                }
                var test = (Button)panel.Children[0];
                var processor = vm.Processor;
                if (working.Contains(processor.InstanceId) || Busy(processor) || processor.IsConnecting || processor.IsClosed)
                    test.IsEnabled = false;
                else if (test.Tag == null) _ = RefreshTestButton(processor, test);
            }
        }
    }

    private async Task RefreshTestButton(MaaProcessor processor, Button button)
    {
        button.Tag = this;
        try
        {
            var device = DispatchDevice.Snapshot(processor);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var running = await DispatchDevice.Running(processor, device, timeout.Token);
            if (!disposed && button.GetVisualRoot() != null)
            {
                DispatchDevice.CheckTarget(processor, device);
                button.IsEnabled = running == false && !Busy(processor) && !SharedBusy(processor, device)
                    && !working.Contains(processor.InstanceId);
            }
        }
        catch { button.IsEnabled = false; }
        finally { button.Tag = null; }
    }

    private async Task TestLaunch(MaaProcessor processor, Button button, TextBlock status)
    {
        if (Busy(processor) || processor.IsConnecting || processor.IsClosed || !working.Add(processor.InstanceId)) return;
        button.IsEnabled = false;
        status.IsVisible = true;
        status.Text = "正在测试…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        operations[processor.InstanceId] = timeout;
        try
        {
            var device = DispatchDevice.Snapshot(processor);
            if (SharedBusy(processor, device)) throw new InvalidOperationException("设备正在执行其他任务");
            await DispatchDevice.Prepare(processor, device, true, timeout.Token, "[测试自动启动] ");
            status.Text = "模拟器已启动，ADB 已就绪。";
        }
        catch (Exception ex)
        {
            status.Text = ex is OperationCanceledException ? "启动测试超时。" : ex.Message;
            processor.AddLog("[测试自动启动] " + status.Text, "OrangeRed");
        }
        finally
        {
            operations.Remove(processor.InstanceId);
            working.Remove(processor.InstanceId);
        }
    }

    private async Task Launch(MaaProcessor processor, JObject job)
    {
        string id = (string)job["id"];
        var cancellation = new CancellationTokenSource();
        operations[processor.InstanceId] = cancellation;
        try
        {
            // Persist the claim before starting anything. An interrupted run must never be replayed.
            Change(processor, id, j => j["state"] = "launching");
            await PrepareDevice(processor, job, cancellation.Token, "[定时派遣] ");
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || (string)Read(processor)?["id"] != id || (string)Read(processor)?["state"] != "launching") return;
            if (!Enabled(processor, Dispatch))
            {
                Change(processor, id, j => { j["state"] = "cancelled"; j["handled"] = true; });
                return;
            }
            if (Busy(processor) || SharedBusy(processor, job))
                throw new InvalidOperationException("启动模拟器期间设备被其他任务占用，请重新预约");
            var tasks = processor.ViewModel?.TaskItemViewModels.Where(t => !t.IsResourceOptionItem
                && t.InterfaceItem?.Entry == "DailyDispatchStart").Take(1).ToList();
            if (tasks == null || tasks.Count != 1) throw new InvalidOperationException("当前实例缺少每日派遣任务");
            DispatchDevice.RebindSavedDevice(processor, job);
            // Discard controller/Agent handles held before the emulator was powered off.
            processor.SetTasker();
            Change(processor, id, j =>
            {
                j["retry_on_disconnected"] = processor.InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnected, false);
                j["auto_detect"] = processor.InstanceConfiguration.GetValue("AutoDetectOnConnectionFailed", true);
                j["before_task"] = processor.InstanceConfiguration.GetValue("BeforeTask", "None");
                j["after_task"] = processor.InstanceConfiguration.GetValue("AfterTask", "None");
                j["policy_saved"] = true;
                j["state"] = "queued";
            });
            processor.InstanceConfiguration.SetValue(ConfigurationKeys.RetryOnDisconnected, false);
            processor.InstanceConfiguration.SetValue("AutoDetectOnConnectionFailed", false);
            processor.InstanceConfiguration.SetValue("BeforeTask", "None");
            processor.InstanceConfiguration.SetValue("AfterTask", "None");
            processor.AddLog("[定时派遣] 启动独立派遣队列，只执行每日派遣。", changeColor: false);
            processor.Start(tasks, ignoreCheckedState: true);
        }
        catch (OperationCanceledException) { Fail(processor, job, "预约已取消或调度器已关闭"); }
        catch (Exception ex) { Fail(processor, job, ex.Message); }
        finally
        {
            operations.Remove(processor.InstanceId);
            cancellation.Dispose();
            working.Remove(processor.InstanceId);
        }
    }

    private async Task Finish(MaaProcessor processor, JObject job)
    {
        try
        {
            if ((bool?)job["policy_saved"] == true)
            {
                if (job["retry_on_disconnected"] != null)
                    processor.InstanceConfiguration.SetValue(ConfigurationKeys.RetryOnDisconnected, (bool)job["retry_on_disconnected"]);
                processor.InstanceConfiguration.SetValue("AutoDetectOnConnectionFailed", (bool?)job["auto_detect"] ?? true);
                processor.InstanceConfiguration.SetValue("BeforeTask", (string)job["before_task"] ?? "None");
                processor.InstanceConfiguration.SetValue("AfterTask", (string)job["after_task"] ?? "None");
                Change(processor, (string)job["id"], j => j["policy_saved"] = false);
            }
            if ((string)job["state"] == "succeeded" && Enabled(processor, AutoClose))
            {
                if (SharedBusy(processor, job)) return;
                try { await CloseEmulator(processor, job, "[定时派遣] "); }
                catch (Exception ex) { processor.AddLog("[定时派遣] 模拟器关闭失败：" + ex.Message, "OrangeRed"); }
            }
            JObject deferred;
            string id = (string)job["id"];
            lock (sync)
            {
                var current = Read(processor);
                if ((string)current?["id"] != id) return;
                deferred = current["next_reservation"] as JObject;
                if (deferred != null) Save(processor, deferred);
                else
                {
                    current["handled"] = true;
                    Save(processor, current);
                }
            }
            if (deferred != null) LogReservation(processor, deferred);
            else if ((string)job["state"] == "succeeded" && job["next_due"] != null)
                SaveCountdown(processor, (JObject)job["config"], (JObject)job["overrides"], (string)job["next_due"],
                    expectedJobId: id);
            else
                processor.AddLog((string)job["state"] == "succeeded"
                    ? "[定时派遣] 本次预约已完成，未登记后续预约。"
                    : "[定时派遣] 本次预约已结束，不会自动重试。", changeColor: false);
        }
        catch (Exception ex)
        {
            Change(processor, (string)job["id"], j => j["handled"] = true);
            processor.AddLog("[定时派遣] 收尾失败：" + ex.Message + "；不重复执行派遣。", "OrangeRed");
        }
        finally { working.Remove(processor.InstanceId); }
    }

    private void Fail(MaaProcessor processor, JObject job, string reason)
    {
        if ((string)Read(processor)?["state"] == "cancelled") return;
        Change(processor, (string)job["id"], j => { j["state"] = "failed"; j["result"] = reason; });
        processor.AddLog("[定时派遣] " + reason + "。本次预约停止，不自动重试。", "OrangeRed");
    }

    private async Task CloseEmulator(MaaProcessor processor, JObject device, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (await DispatchDevice.Running(processor, device, timeout.Token) != true) return;
        if (disposed || Busy(processor) || SharedBusy(processor, device) || !Enabled(processor, AutoClose)) return;
        DispatchDevice.CheckTarget(processor, device);
        processor.SetTasker();
        await DispatchDevice.Close(processor, device, timeout.Token);
        processor.AddLog(label + "任务完成，已请求关闭当前模拟器。", changeColor: false);
    }

    private void RefreshControllerLabels()
    {
        var shown = new HashSet<ComboBox>();
        var views = Instances.TopLevel?.GetVisualDescendants().OfType<TaskQueueView>().ToArray()
            ?? Array.Empty<TaskQueueView>();
        foreach (var view in views)
        {
            if (view.DataContext is not TaskQueueViewModel vm || !vm.IsConnected
                || vm.CurrentController != MaaControllerTypes.Adb) continue;
            var active = vm.Processor.Config.AdbDevice;
            if (string.IsNullOrWhiteSpace(active.AdbSerial)) continue;
            string name = string.IsNullOrWhiteSpace(active.Name) ? "ADB" : active.Name.Split('-')[0].Trim();
            var index = DeviceDisplayConverter.GetFirstEmulatorIndex(active.Config);
            string text = $"{name} ({active.AdbSerial})" + (index < 0 ? "" : $" [{index}]");
            foreach (var box in view.GetVisualDescendants().OfType<ComboBox>().Where(box =>
                ReferenceEquals(box.ItemsSource, vm.Devices) && box.SelectedItem == null))
            {
                // Selection can be empty while the processor still owns a connected controller.
                // Show the active target without firing native selection, reconnect or discovery.
                shown.Add(box);
                if (controllerLabels.TryGetValue(box, out var current) && current.Text == text) continue;
                RestoreControllerLabel(box);
                var placeholder = box.SetValue(ComboBox.PlaceholderTextProperty, text, BindingPriority.Animation);
                var tip = box.SetValue(ToolTip.TipProperty, text, BindingPriority.Animation);
                controllerLabels.Add(box, (text, placeholder, tip));
            }
        }
        foreach (var box in controllerLabels.Keys.Where(box => !shown.Contains(box)).ToArray())
            RestoreControllerLabel(box);
    }

    private void RestoreControllerLabel(ComboBox box)
    {
        if (!controllerLabels.TryGetValue(box, out var label)) return;
        label.Tip?.Dispose();
        label.Placeholder?.Dispose();
        controllerLabels.Remove(box);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var operation in operations.Values) operation.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            timer.Stop(); timer.Tick -= Tick;
            foreach (var task in startupTasks) task.PropertyChanged -= KeepStartupChecked;
            startupTasks.Clear();
            foreach (var box in startupCheckboxes.Keys.ToArray()) ReleaseStartupCheckbox(box);
            foreach (var panel in testPanels)
                if (panel.Parent is StackPanel parent) parent.Children.Remove(panel);
            testPanels.Clear();
            foreach (var toggle in dispatchToggles.Keys.ToArray()) UnbindDispatchToggle(toggle);
            foreach (var grid in dispatchClocks.Keys.ToArray()) HideDispatchClock(grid);
            foreach (var button in dispatchCancelButtons.ToArray()) RemoveDispatchCancelButton(button);
            foreach (var box in controllerLabels.Keys.ToArray()) RestoreControllerLabel(box);
        });
    }
}

internal static class DispatchDevice
{
    private static AdbDeviceInfo SavedDevice(MaaProcessor processor) =>
        processor.InstanceConfiguration.GetValue("RememberAdb", true)
        && processor.InstanceConfiguration.TryGetValue<AdbDeviceInfo>("AdbDevice", out var saved,
            new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>())
            ? saved : null;

    private static (string Adb, string Serial, string Config) SelectedDevice(MaaProcessor processor)
    {
        var active = processor.Config.AdbDevice;
        if (!string.IsNullOrWhiteSpace(active.AdbSerial)) return (active.AdbPath, active.AdbSerial, active.Config);
        var saved = SavedDevice(processor);
        return (saved?.AdbPath ?? "", saved?.AdbSerial ?? "", saved?.Config ?? "{}");
    }

    private static string AdbExecutable(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);
        if (path != "adb" && path != "adb.exe") return "";
        var fileName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName)).FirstOrDefault(File.Exists) ?? "";
    }

    internal static JObject Snapshot(MaaProcessor processor)
    {
        var selected = SelectedDevice(processor);
        string adb = AdbExecutable(selected.Adb);
        if (processor.ViewModel?.CurrentController != MaaControllerTypes.Adb
            || adb.Length == 0 || string.IsNullOrWhiteSpace(selected.Serial))
            throw new InvalidOperationException("请先选择并保存要使用的 ADB 设备");
        return new JObject { ["adb"] = adb, ["serial"] = selected.Serial };
    }

    internal static void RebindSavedDevice(MaaProcessor processor, JObject target)
    {
        CheckTarget(processor, target);
        var active = processor.Config.AdbDevice;
        if (!string.IsNullOrWhiteSpace(active.AdbSerial)) return;
        var saved = SavedDevice(processor);
        if (saved == null) throw new InvalidOperationException("已保存的 ADB 设备配置不再可用");
        active.Name = saved.Name;
        active.AdbPath = (string)target["adb"];
        active.AdbSerial = saved.AdbSerial;
        active.Config = saved.Config;
        active.Info = saved;
    }

    internal static void CheckTarget(MaaProcessor processor, JObject target)
    {
        var current = Snapshot(processor);
        if ((string)current["serial"] != (string)target["serial"] || !string.Equals(
            Path.GetFullPath((string)current["adb"]), Path.GetFullPath((string)target["adb"]),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("当前 ADB 设备与任务登记的设备不一致");
    }

    private static string[] Resolve(MaaProcessor processor, bool native)
    {
        var device = SelectedDevice(processor);
        var config = processor.InstanceConfiguration;
        string path = native ? config.GetValue(ConfigurationKeys.SoftwarePath, "") : "";
        string args = native ? config.GetValue(ConfigurationKeys.EmulatorConfig, "") : "";
        if (native && string.IsNullOrWhiteSpace(path))
        {
            if (config.GetValue("TkfmSavedEmulatorSerial", "") == device.Serial)
            {
                path = config.GetValue("TkfmSavedEmulatorPath", "");
                args = config.GetValue("TkfmSavedEmulatorArguments", "");
            }
        }
        return AppDomain.CurrentDomain.GetData("TKFMaa.ResolveEmulatorConnection") is
            Func<string, string, string, bool, string, string, string[]> resolve
            ? resolve(AdbExecutable(device.Adb), device.Serial, device.Config ?? "{}", true, path, args)
            : Array.Empty<string>();
    }

    private static bool MuMu(string[] command, out string manager, out string index)
    {
        manager = command.FirstOrDefault() ?? "";
        index = "";
        if (command.Length != 2 || !string.Equals(Path.GetFileName(manager), "MuMuManager.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var match = Regex.Match(command[1], @"^control -v ([0-9]+) launch$");
        if (!match.Success) return false;
        index = match.Groups[1].Value;
        return true;
    }

    private static async Task<bool> MuMuRunning(string manager, string index, string serial, CancellationToken token, bool checkLaunchError = false)
    {
        var info = ParseManager(await Command(manager, new[] { "info", "-v", index }, token));
        if ((string)info["index"] != index || info["is_process_started"]?.Type != JTokenType.Boolean)
            throw new InvalidOperationException("无法确认所选模拟器的运行状态");
        if (checkLaunchError && ((int?)info["launch_err_code"] ?? 0) != 0)
            throw new InvalidOperationException("模拟器启动失败：" + info["launch_err_msg"]);
        bool running = (bool)info["is_process_started"];
        string host = (string)info["adb_host_ip"];
        int port = (int?)info["adb_port"] ?? 0;
        if (running && !string.IsNullOrWhiteSpace(host) && port > 0
            && Uri.TryCreate("tcp://" + serial, UriKind.Absolute, out var endpoint) && endpoint.Port > 0
            && (endpoint.Port != port || !(string.Equals(endpoint.Host, host, StringComparison.OrdinalIgnoreCase)
                || (endpoint.IsLoopback && (host == "127.0.0.1" || host == "localhost" || host == "::1")))))
            throw new InvalidOperationException("模拟器的 ADB 地址与当前设备不符");
        return running;
    }

    private static JObject ParseManager(string output)
    {
        var result = JObject.Parse(output);
        if (((int?)result["error_code"] ?? 0) != 0 || ((int?)result["errcode"] ?? 0) != 0)
            throw new InvalidOperationException("模拟器管理命令失败：" + ((string)result["error_msg"] ?? (string)result["errmsg"] ?? output));
        return result;
    }

    private static async Task<bool> Connected(JObject device, CancellationToken token)
    {
        string adb = (string)device["adb"], serial = (string)device["serial"];
        var devices = await Command(adb, new[] { "devices" }, token);
        return devices.Split('\n').Any(line => line.TrimStart().StartsWith(serial + "\t", StringComparison.Ordinal)
            || line.TrimStart().StartsWith(serial + " ", StringComparison.Ordinal));
    }

    private static async Task<bool> Ready(JObject device, CancellationToken token)
    {
        string adb = (string)device["adb"], serial = (string)device["serial"];
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
        probe.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            if (Uri.TryCreate("tcp://" + serial, UriKind.Absolute, out var endpoint) && endpoint.Port > 0)
                await Command(adb, new[] { "connect", serial }, probe.Token);
            var output = await Command(adb, new[] { "-s", serial, "shell", "getprop", "sys.boot_completed" }, probe.Token);
            return output.Trim() == "1";
        }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    internal static async Task<bool?> Running(MaaProcessor processor, JObject device, CancellationToken token)
    {
        CheckTarget(processor, device);
        var automatic = Resolve(processor, false);
        string serial = (string)device["serial"];
        if (MuMu(automatic, out var manager, out var index)) return await MuMuRunning(manager, index, serial, token);
        if (await Connected(device, token)) return true;
        if (Regex.IsMatch(serial, @"^emulator-[0-9]+$"))
        {
            int port = int.Parse(serial.Substring("emulator-".Length), CultureInfo.InvariantCulture);
            return await Listening("127.0.0.1", port, token);
        }
        if (Uri.TryCreate("tcp://" + serial, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback && endpoint.Port > 0)
            return await Listening(endpoint.Host.Trim('[', ']'), endpoint.Port, token);
        return null;
    }

    private static async Task<bool?> Listening(string host, int port, CancellationToken token)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await client.ConnectAsync(host, port, timeout.Token); return true; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    internal static async Task Prepare(MaaProcessor processor, JObject device, bool allowStart, CancellationToken token, string label)
    {
        CheckTarget(processor, device);
        string serial = (string)device["serial"];
        if (await Ready(device, token))
        {
            CheckTarget(processor, device);
            processor.AddLog(label + "当前 ADB 设备已就绪。", changeColor: false);
            return;
        }
        if (!allowStart) throw new InvalidOperationException("ADB 设备未就绪，自动启动模拟器已关闭");
        var running = await Running(processor, device, token);
        CheckTarget(processor, device);
        var command = Resolve(processor, true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        Process launcher = null;
        try
        {
            if (running != true)
            {
                if (command.Length != 2) throw new InvalidOperationException("未找到当前 ADB 设备对应的启动程序");
                if (running == null) throw new InvalidOperationException("无法确认模拟器是否关闭");
                if (MuMu(command, out var manager, out var index))
                {
                    var output = await Command(manager, new[] { "control", "-v", index, "launch" }, timeout.Token);
                    if (output.TrimStart().StartsWith("{")) ParseManager(output);
                }
                else
                    launcher = Process.Start(new ProcessStartInfo(command[0])
                    {
                        Arguments = command[1], UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(command[0]))
                    }) ?? throw new InvalidOperationException("无法运行模拟器启动程序");
                processor.AddLog(label + "已请求启动模拟器，等待 ADB 就绪。", changeColor: false);
            }
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                CheckTarget(processor, device);
                if (launcher != null && launcher.HasExited && launcher.ExitCode != 0)
                    throw new InvalidOperationException("模拟器启动程序返回错误 " + launcher.ExitCode);
                if (await Ready(device, timeout.Token))
                {
                    CheckTarget(processor, device);
                    if (MuMu(Resolve(processor, false), out var manager, out var index)
                        && !await MuMuRunning(manager, index, serial, timeout.Token, checkLaunchError: true))
                        throw new InvalidOperationException("ADB 已连通，但所选模拟器实例没有启动");
                    processor.AddLog(label + "ADB 设备 " + serial + " 已就绪。", changeColor: false);
                    return;
                }
                await Task.Delay(2000, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("等待当前 ADB 设备就绪超过三分钟");
        }
        finally { launcher?.Dispose(); }
    }

    internal static async Task Close(MaaProcessor processor, JObject device, CancellationToken token)
    {
        CheckTarget(processor, device);
        if (MuMu(Resolve(processor, false), out var manager, out var index))
        {
            var result = await Command(manager, new[] { "control", "-v", index, "shutdown" }, token);
            if (result.TrimStart().StartsWith("{")) ParseManager(result);
            return;
        }
        string serial = (string)device["serial"];
        if (Regex.IsMatch(serial, @"^emulator-[0-9]+$") && await Connected(device, token))
        {
            CheckTarget(processor, device);
            await Command((string)device["adb"], new[] { "-s", serial, "emu", "kill" }, token);
            return;
        }
        throw new InvalidOperationException("当前模拟器没有可确认的单实例关闭命令，已保留运行状态");
    }

    private static async Task<string> Command(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动管理命令"))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(15000, cancellation)) != exit)
            {
                try { process.Kill(); } catch { }
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("管理命令超过15秒未返回");
            }
            await exit;
            string stdout = await output, stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("管理命令失败：" + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
            return stdout;
        }
    }
}
