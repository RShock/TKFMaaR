using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Wraps a complete module, never individual screen transitions or native event callbacks.
public sealed class TkfmRunWithRecovery : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmRunWithRecovery";
    internal const string EnabledKey = "TkfmRestartFailedStep";
    internal const string FailureBehaviorKey = "TkfmFailureBehavior";
    internal const string RestartBehavior = "restart";
    internal const string PauseBehavior = "pause";
    private static readonly ConcurrentDictionary<IMaaTasker, byte> Running =
        new ConcurrentDictionary<IMaaTasker, byte>();

    public TkfmRunWithRecovery()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.GameOptionsMigration";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmGameOptionsMigration());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        MaaProcessor processor = null;
        bool registered = false;
        bool continueAfterFailure = false;
        string failureBehavior = RestartBehavior;
        try
        {
            processor = MaaProcessor.Processors.ToArray().FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
            if (processor == null)
                throw new InvalidOperationException("无法定位所属实例，拒绝执行任务");
            var param = JObject.Parse(args.ActionParam);
            if ((string)param["mode"] == "launch_game")
                return LaunchGame(context, processor);
            // A recognized login interruption runs a bounded child state machine.
            // It does not enter or initialize the business wrapper a second time.
            if ((string)param["mode"] == "resume_login") return ResumeLogin(context);
            if (!Running.TryAdd(context.Tasker, 0))
                return FinishFailure(processor, "异常恢复入口发生递归，拒绝再次进入。", false);
            registered = true;

            // Read policy once per module. Runtime edits apply to later modules;
            // retries and a paused current module retain the same policy and quotas.
            failureBehavior = GetFailureBehavior(processor);
            bool restartOnFailure = failureBehavior == RestartBehavior;
            continueAfterFailure = processor.InstanceConfiguration.GetValue(
                ConfigurationKeys.ContinueRunningWhenError, false);
            string entry = (string)param["entry"];
            string label = (string)param["label"] ?? args.NodeName;
            string? homeRetryPrefix = (string?)param["home_retry_prefix"];
            string[] successAnyHit = (param["success_any_hit"] as JArray)?.Values<string>()
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(entry) || entry == args.NodeName
                || !context.GetNodeData(entry, out var unused))
                throw new InvalidOperationException("任务主体不存在或指向恢复入口自身：" + entry);

            processor.AddLog("[异常恢复] " + label + "：开始执行。", changeColor: false);
            // Install error branches on this module's context, before preparation and retries.
            // JumpBack retains the existing pipeline stack and business bookkeeping.
            foreach (string popup in new[] { "TkfmRecoveryWebPageUnavailable", "TkfmRecoveryWebPageUnavailablePriority",
                "TkfmRecoveryLoginBegin", "TkfmRecoveryResumeLogin", "TkfmRecoveryLoginSucceeded",
                "TkfmRecoveryLoginTapTitle", "TkfmRecoveryLoginCancelExit", "TkfmRecoveryLoginNetworkConfirm",
                "TkfmRecoveryLoginAdvertisement", "TkfmRecoveryLoginAnnouncement",
                "TkfmRecoveryLoginEventSignIn", "TkfmRecoveryLoginMonthlyCard",
                "TkfmRecoveryReturnTitle",
                "TkfmRecoverySessionExpired", "TkfmRecoveryAdvertisement",
                "TkfmRecoveryAnnouncement",
                "TkfmRecoveryEventSignIn", "TkfmRecoverySignIn", "GameCloseSignIn",
                "TkfmRecoveryBackButton", "TkfmRecoveryReturnMap", "TkfmRecoverySystemBack", "TkfmRecoveryReturnHomeRequested", "GameLoginBackFallback",
                "GameLoginExistingGiftFallback" })
                if (context.GetHitCount(popup, out var popupCount) && popupCount > 0 && !context.ClearHitCount(popup))
                    throw new InvalidOperationException("无法初始化弹窗恢复额度：" + popup);
            if (!context.OverridePipeline(PopupRecoveryOverride(context, homeRetryPrefix).ToString(Formatting.None)))
                throw new InvalidOperationException("无法安装通用弹窗恢复分支");
            // One-time module preparation is outside both recovery attempts.
            // In particular, daily ticket reservations survive a game restart.
            string initialize = (string)param["initialize"];
            if (!string.IsNullOrWhiteSpace(initialize)
                && !RunOnce(context, processor, initialize, InitializationOverride(processor, initialize)))
                throw new InvalidOperationException("无法初始化任务账目：" + initialize);
            string? nonRetryableHit = (string?)param["non_retryable_hit"];
            ulong previousNonRetryableHits = 0;
            if (!string.IsNullOrWhiteSpace(nonRetryableHit))
                context.GetHitCount(nonRetryableHit, out previousNonRetryableHits);
            if (EnsureGameRunning(context, processor)
                && RunAttempt(context, processor, entry, successAnyHit))
                return !Cancelled(context, processor);
            if (Cancelled(context, processor)) return false;
            RecordFailure(context, label + "首次执行失败");
            if (!string.IsNullOrWhiteSpace(nonRetryableHit)
                && context.GetHitCount(nonRetryableHit, out var nonRetryableHits)
                && nonRetryableHits > previousNonRetryableHits)
            {
                string reason = label + "尚未满足游戏内的前置条件。";
                return restartOnFailure
                    ? FinishFailure(processor, reason, continueAfterFailure)
                    : PauseForFailure(processor, reason, continueAfterFailure);
            }
            bool returnedHome = false;
            if (!string.IsNullOrWhiteSpace(homeRetryPrefix) && WebPageFaultCount(context) == 0
                && context.GetHitCount("TkfmRecoveryReturnHomeRequested", out var returnRequests)
                && returnRequests > 0)
            {
                processor.AddLog("[异常恢复] " + label + "：尝试返回主页，最多使用三次安卓返回键。", changeColor: false);
                returnedHome = TryReturnHome(context, processor);
            }
            if (Cancelled(context, processor)) return false;

            if (!returnedHome)
            {
                bool webPageUnavailable = WebPageFaultCount(context) > 0;
                if (!restartOnFailure && !webPageUnavailable)
                    return PauseForFailure(processor, label + "执行失败。", continueAfterFailure);

                processor.AddLog("[异常恢复] " + label + (webPageUnavailable
                    ? "：识别到网页无法打开，结束游戏进程并重新登录。"
                    : "：结束游戏进程并重新登录。"), "OrangeRed");
                // The login module already stops and starts the game in its own entry.
                if (args.NodeName != "GameLoginStart")
                {
                    ResetNavigationHits(context, "Game");
                    string loginOverride = LoginOverride(context, processor).ToString(Formatting.None);
                    if (!RunAttempt(context, processor, "TkfmRecoveryRestartGame",
                        new[] { "GameMainReady" }, loginOverride))
                    {
                        if (Cancelled(context, processor)) return false;
                        RecordFailure(context, label + "的重启或登录失败");
                        return FinishFailure(processor, label + "的重启或登录失败。", continueAfterFailure);
                    }
                    MarkGamePrepared(processor);
                }
            }

            // Both methods use the same navigation reset, entry, and completion checks.
            if (Cancelled(context, processor)) return false;
            ResetNavigationHits(context);
            string retryOverride = RetryOverride(entry).ToString(Formatting.None);
            processor.AddLog("[异常恢复] " + label + "：从任务入口重新执行（仅一次）。", changeColor: false);
            if (RunAttempt(context, processor, entry, successAnyHit, retryOverride))
            {
                if (Cancelled(context, processor)) return false;
                processor.AddLog("[异常恢复] " + label + "重试成功。", changeColor: false);
                return true;
            }
            if (Cancelled(context, processor)) return false;
            RecordFailure(context, label + "重试后仍异常");
            return FinishFailure(processor, label + "重试后仍异常，恢复重跑额度已用尽。", continueAfterFailure);
        }
        catch (Exception ex)
        {
            if (processor == null)
            {
                LoggerHelper.Error("TKFMaa 异常恢复：" + ex.Message);
                return false;
            }
            if (Cancelled(context, processor)) return false;
            LoggerHelper.Error("TKFMaa 恢复调度异常", ex);
            RecordFailure(context, "恢复调度失败：" + ex.Message);
            string reason = "恢复调度失败：" + ex.Message + "；不递归重启。";
            return failureBehavior == PauseBehavior
                ? PauseForFailure(processor, reason, continueAfterFailure)
                : FinishFailure(processor, reason, continueAfterFailure);
        }
        finally
        {
            if (registered) Running.TryRemove(context.Tasker, out var unused);
        }
    }

    private static void RecordFailure(IMaaContext context, string reason)
    {
        LoggerHelper.Error("TKFMaa 任务故障：" + reason);
        try
        {
            // Reuse the most recent task frame before recovery leaves the failure screen.
            using var image = new MaaImageBuffer();
            if (context.Tasker.Controller?.GetCachedImage(image) != true
                || image.IsEmpty || !image.TryGetEncodedData(out Stream encoded)) return;
            using (encoded)
            {
                string directory = Path.Combine(AppPaths.InstallRoot, "debug", "on_error");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Guid.NewGuid():N}_TkfmRecovery.png");
                string pending = file + ".tmp";
                try
                {
                    using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        encoded.CopyTo(output);
                    File.Move(pending, file);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning("保存故障现场截图失败：" + ex.Message);
        }
    }

    private static string GetFailureBehavior(MaaProcessor processor)
    {
        string configured = processor.InstanceConfiguration.GetValue(FailureBehaviorKey, string.Empty);
        if (configured.Equals(RestartBehavior, StringComparison.OrdinalIgnoreCase)
            || configured.Equals(PauseBehavior, StringComparison.OrdinalIgnoreCase))
            return configured.ToLowerInvariant();

        string migrated = processor.InstanceConfiguration.GetValue(EnabledKey, true)
            ? RestartBehavior : PauseBehavior;
        processor.InstanceConfiguration.SetValue(FailureBehaviorKey, migrated);
        return migrated;
    }

    private static bool PauseForFailure(MaaProcessor processor, string reason, bool continueAfterFailure)
    {
        processor.AddLog("[故障暂停] " + reason + " 不重启，等待人工处理。", "OrangeRed");
        try
        {
            object? service = AppDomain.CurrentDomain.GetData("TKFMaa.TaskPause.Service");
            var method = service?.GetType().GetMethod("PauseForFailure",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (service != null && method != null)
            {
                object? result = method.Invoke(service, new object[] { processor, reason });
                if (result is bool paused && paused) return false;
            }
        }
        catch (TargetInvocationException ex)
        {
            processor.AddLog("[故障暂停] 暂停服务异常：" + (ex.InnerException?.Message ?? ex.Message), "OrangeRed");
        }
        catch (Exception ex)
        {
            processor.AddLog("[故障暂停] 暂停服务异常：" + ex.Message, "OrangeRed");
        }
        return FinishFailure(processor, reason + " 暂停服务不可用。", continueAfterFailure);
    }

    private static bool Cancelled(IMaaContext context, MaaProcessor processor) =>
        context.IsCancellationRequested || processor.CancellationTokenSource?.IsCancellationRequested == true;

    private static ulong WebPageFaultCount(IMaaContext context)
    {
        context.GetHitCount("TkfmRecoveryWebPageUnavailable", out var errors);
        context.GetHitCount("TkfmRecoveryWebPageUnavailablePriority", out var priority);
        return errors + priority;
    }

    private static string InitializationOverride(MaaProcessor processor, string entry)
    {
        if (entry != "DailyAffairsInitialize") return "{}";
        return new JObject
        {
            [entry] = new JObject
            {
                ["action"] = new JObject
                {
                    ["param"] = new JObject
                    {
                        ["custom_action_param"] = new JObject
                        {
                            ["instance_id"] = processor.InstanceId,
                            ["config_dir"] = AppPaths.ConfigDirectory,
                            ["logs_dir"] = AppPaths.LogsDirectory
                        }
                    }
                }
            }
        }.ToString(Formatting.None);
    }

    private static bool RunOnce(IMaaContext context, MaaProcessor processor, string entry, string overrides = "{}")
    {
        if (Cancelled(context, processor)) return false;
        ulong webFaultsBefore = WebPageFaultCount(context);
        try
        {
            // RunTask clones pipeline overrides while sharing hit counts/anchors. Explicitly reset only
            // navigation counts on retry; do not erase consumption limits or the purchased-stamina anchor.
            var detail = context.RunTask(entry, overrides);
            // A terminal web-error route requests the outer module's one restart.
            // Never mistake ending a nested pipeline for business completion.
            if (WebPageFaultCount(context) > webFaultsBefore) return false;
            return !Cancelled(context, processor) && detail?.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor))
                processor.AddLog("[异常恢复] " + entry + "执行异常：" + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static bool RunAttempt(IMaaContext context, MaaProcessor processor, string entry,
        IReadOnlyList<string> successAnyHit, string overrides = "{}")
    {
        if (successAnyHit.Count == 0) return RunOnce(context, processor, entry, overrides);

        var before = new ulong[successAnyHit.Count];
        for (int i = 0; i < successAnyHit.Count; i++)
            if (context.GetHitCount(successAnyHit[i], out var count)) before[i] = count;

        if (!RunOnce(context, processor, entry, overrides)) return false;
        for (int i = 0; i < successAnyHit.Count; i++)
            if (context.GetHitCount(successAnyHit[i], out var count) && count > before[i]) return true;

        processor.AddLog("[异常恢复] " + entry + "返回成功，但未命中完成节点："
            + string.Join(" / ", successAnyHit) + "。安全返回主页不等于任务完成。", "OrangeRed");
        return false;
    }

    private static bool TryReturnHome(IMaaContext context, MaaProcessor processor)
    {
        var overrides = new JObject
        {
            ["TkfmRecoverySystemBack"] = new JObject
            {
                ["recognition"] = new JObject
                {
                    ["type"] = "Custom",
                    ["param"] = new JObject
                    {
                        ["custom_recognition"] = "TaskCompletionGuard",
                        ["custom_recognition_param"] = new JObject
                        {
                            ["required"] = new JArray("__TkfmRecoveryAnyScreen"),
                            ["absent"] = new JArray("CommonMainReady", "CommonWaitLoading",
                                "TkfmRecoveryLoginTarget", "TkfmRecoveryReturnTitleTarget", "TkfmRecoveryReturnMap", "GameCloseGift",
                                "GameCloseAnnouncement", "GameCloseEventSignIn", "TkfmRecoveryMonthlyCardTarget")
                        }
                    }
                },
                ["focus"] = new JObject { ["Node.Action.Starting"] = "异常恢复：发送一次安卓返回键，然后重新确认主页。" }
            }
        };
        return RunAttempt(context, processor, "TkfmRecoveryReturnHome",
            new[] { "TkfmRecoveryReturnHomeConfirmed" }, overrides.ToString(Formatting.None));
    }

    private static void ResetNavigationHits(IMaaContext context, string? prefix = null)
    {
        if (!context.GetNodeData("TkfmRecoveryConfig", out var raw))
            throw new InvalidOperationException("缺少公共恢复配置");
        var config = JObject.Parse(raw)["attach"];
        var preserve = new HashSet<string>((config?["preserve_hits"] as JArray ?? new JArray()).Values<string>());
        foreach (var name in context.Tasker.Resource.NodeList)
        {
            if (preserve.Contains(name) || (prefix != null && !name.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            if (context.GetHitCount(name, out var count) && count > 0 && !context.ClearHitCount(name))
                throw new InvalidOperationException("无法重置导航次数：" + name);
        }
    }

    private static JObject PopupRecoveryOverride(IMaaContext context, string? homeRetryPrefix)
    {
        var overrides = new JObject();
        var observed = new[] {
            "TkfmRecoveryWebPageUnavailablePriority", "TkfmRecoverySessionExpiredPriority",
            "TkfmRecoveryLoginObserved",
            "TkfmRecoveryReturnTitleObserved",
            "TkfmRecoveryAnnouncementObserved", "TkfmRecoveryEventSignInObserved",
            "TkfmRecoverySignInObserved", "TkfmRecoveryAdvertisementObserved"
        };
        var nodes = new Dictionary<string, JObject>();
        foreach (string name in context.Tasker.Resource.NodeList)
        {
            // Recovery branches must not recursively recover themselves. Pure recognition
            // helpers have no outgoing flow and are not task failure boundaries.
            if (name.StartsWith("TkfmRecovery", StringComparison.Ordinal)
                || name == "TkfmTaskFailure" || !context.GetNodeData(name, out var raw)) continue;
            nodes[name] = JObject.Parse(raw);
        }
        var responseWaits = new HashSet<string>(nodes.Where(item => IsResponseWait(item.Value))
            .Select(item => item.Key));
        foreach (var item in nodes)
        {
            string name = item.Key;
            var node = item.Value;
            if ((string)node["action"]?["param"]?["custom_action"] == "TkfmRunWithRecovery") continue;
            var next = NodeList(node["next"]);
            var original = NodeList(node["on_error"]);
            if (next.Count == 0 && original.Count == 0) continue;
            // An explicit terminal failure should leave the current game screen visible.
            if (original.Count == 0 && next.Count == 1 && RouteName(next[0]) == "TkfmTaskFailure"
                && (long?)node["timeout"] == 1) continue;
            // Ordinary pages and real ads can share the complete back button.
            // GameCloseGift additionally requires the advertisement tabs; generic
            // back navigation is only an unmatched-timeout fallback.
            var errors = new JArray("TkfmRecoveryWebPageUnavailable",
                "[JumpBack]TkfmRecoveryLoginObserved",
                "[JumpBack]TkfmRecoveryReturnTitle",
                "[JumpBack]TkfmRecoverySessionExpired", "[JumpBack]TkfmRecoveryAnnouncement",
                "[JumpBack]TkfmRecoveryEventSignIn", "[JumpBack]TkfmRecoverySignIn");
            bool retryFromHome = !string.IsNullOrWhiteSpace(homeRetryPrefix)
                && name.StartsWith(homeRetryPrefix, StringComparison.Ordinal);
            errors.Add("[JumpBack]TkfmRecoveryReturnMap");
            if (!retryFromHome) errors.Add("[JumpBack]TkfmRecoveryBackButton");
            errors.Add("[JumpBack]TkfmRecoveryAdvertisement");
            if (!retryFromHome) errors.Add("[JumpBack]TkfmRecoverySystemBack");
            foreach (var route in original) errors.Add(route.DeepClone());
            if (retryFromHome) errors.Add("TkfmRecoveryReturnHomeRequested");
            if (retryFromHome || original.Count == 0) errors.Add("TkfmRecoveryUnhandled");
            var patch = new JObject { ["on_error"] = errors };
            if (next.Count > 0)
            {
                // Each native recognition round tries business targets, then known
                // interruptions on the same frame. No hit means another round, not a click.
                // Remove the old leading priority routes before placing them after business.
                next = new JArray(next.Where(route => !observed.Contains(RouteName(route)))
                    .Select(route => route.DeepClone()));
                int insertion = next.Count;
                for (int i = 0; i < next.Count; i++)
                {
                    if (responseWaits.Contains(RouteName(next[i])))
                    {
                        // A bookkeeping-only wait would otherwise match every frame and
                        // conceal interruptions until its independent response timer expires.
                        insertion = i;
                        break;
                    }
                }
                foreach (string recovery in observed)
                    next.Insert(insertion++, recovery == "TkfmRecoveryWebPageUnavailablePriority"
                        ? new JValue(recovery) : new JValue("[JumpBack]" + recovery));
                patch["next"] = next;
                // Twenty seconds of unmatched rounds precede navigation/system-back
                // fallbacks. Preserve deliberate terminal failure nodes (timeout 0/1).
                if ((long?)node["timeout"] != 0 && (long?)node["timeout"] != 1)
                    patch["timeout"] = 20000;
            }
            overrides[name] = patch;
        }
        // Recovery may finish a login subflow, but must never stop its parent task.
        // Only the recognized home page completes a login subflow; no advertisement is required.
        overrides["GameMainReady"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
        };
        return overrides;
    }

    internal static bool ResumeLogin(IMaaContext context)
    {
        // This is a bounded child login, not a recursive business-task retry.
        // It remains available when ordinary popup/back budgets are exhausted.
        var processor = MaaProcessor.Processors.ToArray().FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
        if (processor == null || Cancelled(context, processor)) return false;
        try
        {
            var overrides = new JObject();
            foreach (string name in new[] {
                "GameLoginDispatch", "GameLoginExistingDispatch", "GameLoginExistingReturnDispatch",
                "GameLoginReconnect", "GameSkipStartupAnimation", "CommonWaitLoading", "GameWaitResourceCheck",
                "GameCloseGift", "GameCloseAnnouncement", "GameCloseEventSignIn", "GameCloseSignIn",
                "GameLoginWaitTapScreen", "TkfmRecoveryReturnMap", "TkfmRecoveryBackButton"
            })
            {
                // No ordinary recovery branches inside this child: every action
                // returns to the dispatcher whose first candidate checks the
                // absolute deadline. Repeated loading never renews that deadline.
                overrides[name] = new JObject
                {
                    ["next"] = new JArray("TkfmRecoveryLoginDispatch"),
                    ["on_error"] = new JArray("TkfmTaskFailure"), ["timeout"] = 1000
                };
            }
            // These are local login controls, so exhausted ordinary popup
            // allowances cannot hide a fresh login's announcement or card.
            // Each attempt gets bounded navigation only; no business quota is reset.
            foreach (var control in new Dictionary<string, ulong> {
                ["TkfmRecoveryLoginDispatch"] = 200,
                ["TkfmRecoveryLoginTapTitle"] = 5, ["TkfmRecoveryLoginCancelExit"] = 3,
                ["TkfmRecoveryLoginNetworkConfirm"] = 2, ["TkfmRecoveryLoginAdvertisement"] = 3,
                ["TkfmRecoveryLoginAnnouncement"] = 3, ["TkfmRecoveryLoginEventSignIn"] = 3,
                ["TkfmRecoveryLoginMonthlyCard"] = 1
            })
            {
                context.GetHitCount(control.Key, out var used);
                overrides[control.Key] = new JObject { ["max_hit"] = used + control.Value };
            }
            bool succeeded = RunAttempt(context, processor, "TkfmRecoveryLoginBegin",
                new[] { "TkfmRecoveryLoginSucceeded" }, overrides.ToString(Formatting.None));
            if (!succeeded && !Cancelled(context, processor))
                processor.AddLog("[异常恢复] 登录接管未能确认主页；保留当前任务账目，交回故障策略处理。", "OrangeRed");
            return succeeded;
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor))
                processor.AddLog("[异常恢复] 登录接管异常：" + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static JArray NodeList(JToken? value) => value is JArray array
        ? (JArray)array.DeepClone() : value == null || value.Type == JTokenType.Null
            ? new JArray() : new JArray(value.DeepClone());

    private static string RouteName(JToken value)
    {
        // GetNodeData returns resolved NodeAttr objects, even for string routes
        // written in source JSON. Keep their jump_back/anchor attributes intact.
        return value.Type == JTokenType.String
            ? Regex.Replace((string)value, @"^(?:\[[^\]]+\])+", "")
            : (string)(value as JObject)?["name"] ?? string.Empty;
    }

    private static bool IsResponseWait(JObject node)
    {
        // Modules declare bookkeeping-only waits themselves. Recovery must not
        // depend on individual Python handler names or their private mode values.
        return (bool?)node["attach"]?["recovery_response_wait"] == true;
    }

    private static JObject RetryOverride(string entry)
    {
        var overrides = new JObject
        {
            ["TkfmRecoveryReturnHomeRequested"] = new JObject { ["enabled"] = false }
        };
        if (entry == "DailyShopStartAttempt")
        {
            // A game restart may rebuild navigation, but cannot issue a new shopping
            // session or replenish its scroll/purchase budgets.
            overrides["DailyShopResetExchangeSession"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "DoNothing" }
            };
        }
        if (entry == "AutoBattleStartAttempt")
        {
            // This legacy entry requires a manually selected activity. Returning to the home page
            // after restart must not masquerade as successfully rebuilding that activity entry.
            overrides[entry] = new JObject
            {
                ["next"] = new JArray("RegularActivityStageListDecision"), ["timeout"] = 10000
            };
            return overrides;
        }
        // Recruitment uses per-attempt anchors shared by recovery subtasks. Navigation hit
        // counts are no longer the quota, and must never be converted back into batch counts.
        return overrides;
    }

    private static JObject LoginOverride(IMaaContext context, MaaProcessor processor)
    {
        string resource = processor.ViewModel?.CurrentResource;
        string package = resource == "官服" ? "com.pinkcore.tkfm"
            : resource == "erolab服务器" ? "com.pinkcore.tkfm.erolabs"
            : throw new InvalidOperationException("无法确定资源类型对应的游戏包名：" + resource);
        return new JObject
        {
            ["TkfmRecoveryRestartGame"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "StopApp", ["param"] = new JObject { ["package"] = package } }
            },
            ["GameLoginLaunch"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "StartApp", ["param"] = new JObject { ["package"] = package } }
            },
            // StopTask propagates to the parent context and would prevent rerunning the failed module.
            // Finish only this login subtask after recognizing Expedition without a known obstruction.
            ["GameMainReady"] = new JObject { ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray() }
        };
    }

    private static bool LaunchGame(IMaaContext context, MaaProcessor processor)
    {
        try
        {
            bool alreadyForeground = OpenGame(processor, "[启动游戏]",
                processor.CancellationTokenSource?.Token ?? CancellationToken.None);
            if (alreadyForeground && !Cancelled(context, processor)) TryStartupRecovery(context);
            return !Cancelled(context, processor);
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor)) processor.AddLog("[启动游戏] 第2步失败：" + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static void TryStartupRecovery(IMaaContext context)
    {
        // Reuse the positive recovery recognizers and actions in a child pipeline.
        // A miss ends the probe quietly, leaving the caller to run normal login.
        string[] steps = {
            "TkfmRecoveryReturnTitle", "TkfmRecoveryAdvertisement", "TkfmRecoveryAnnouncement",
            "TkfmRecoveryEventSignIn", "TkfmRecoverySignIn", "TkfmRecoveryReturnMap",
            "TkfmRecoveryBackButton", "GameLoginCastleFallback"
        };
        var next = new JArray("TkfmRecoveryReturnHomeConfirmed");
        var overrides = new JObject
        {
            ["TkfmRecoveryReturnHome"] = new JObject
            {
                ["next"] = next, ["on_error"] = new JArray(), ["timeout"] = 1500, ["rate_limit"] = 350
            },
            ["TkfmRecoveryReturnHomeConfirmed"] = new JObject
            {
                ["recognition"] = new JObject
                {
                    ["type"] = "And", ["param"] = new JObject { ["all_of"] = new JArray("GameMainReady") }
                }
            }
        };
        foreach (var step in steps)
        {
            next.Add("[JumpBack]" + step);
            overrides[step] = new JObject
            {
                ["next"] = new JArray(), ["on_error"] = new JArray(), ["focus"] = JValue.CreateNull(),
                ["max_hit"] = step == "TkfmRecoveryBackButton" ? 3 : step == "TkfmRecoverySignIn" ? 1 : 2
            };
        }
        // Each recognized return button can lead to another page with a return button.
        // Scope its quota separately from transient popups in this startup probe.
        overrides["TkfmRecoveryBackButton"]["recognition"] = new JObject
        {
            ["type"] = "Custom",
            ["param"] = new JObject
            {
                ["custom_recognition"] = "TaskCompletionGuard",
                ["custom_recognition_param"] = new JObject
                {
                    ["required"] = new JArray("TkfmRecoveryBackButtonTarget"),
                    ["absent"] = new JArray("CommonWaitLoading", "TkfmRecoveryLoginTarget", "TkfmRecoveryReturnTitleTarget",
                        "TkfmRecoveryExitGameTarget", "GameCloseGift", "GameCloseAnnouncement", "GameCloseEventSignIn",
                        "TkfmRecoveryMonthlyCardTarget")
                }
            }
        };
        // Missing a candidate is expected. End normally so the timeout diagnostics
        // cannot display a recognition-failure message for the optional probe.
        next.Add("GameLoginStartupProbeDone");
        try { context.RunTask("TkfmRecoveryReturnHome", overrides.ToString(Formatting.None)); }
        catch (Exception) { /* The ordinary login dispatcher rechecks the current page. */ }
    }

    private static void RequireAdb(string adb, string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || !File.Exists(adb))
            throw new InvalidOperationException("当前配置缺少有效的 ADB 程序或设备地址，请先选择对应设备");
    }

    private static bool OpenGame(MaaProcessor processor, string label, CancellationToken cancellation)
    {
        var device = processor.Config.AdbDevice;
        string adb = device.AdbPath, serial = device.AdbSerial;
        RequireAdb(adb, serial);
        string package = GamePackage(processor);
        string Shell(params string[] command) => RunCommand(adb, new[] { "-s", serial, "shell" }.Concat(command), cancellation);
        bool InForeground() => Regex.IsMatch(Shell("dumpsys", "activity", "activities"),
            @"(?m)^\s*(?:topResumedActivity|mResumedActivity)\b[^\r\n]*?\s" + Regex.Escape(package) + "/");

        processor.AddLog(label + " 第2步：检查并打开游戏 " + package + "。", changeColor: false);
        bool alreadyForeground = InForeground();
        if (!alreadyForeground)
        {
            string resolved = Shell("cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN",
                "-c", "android.intent.category.LAUNCHER", package);
            string activity = Regex.Match(resolved, @"(?m)^" + Regex.Escape(package) + @"/[\w.$]+\s*$").Value.Trim();
            if (activity.Length == 0)
                throw new InvalidOperationException("找不到游戏启动入口，请确认所选服务器的游戏已安装。ADB 返回：" + resolved.Trim());
            string result = Shell("am", "start", "-W", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", "-n", activity);
            if (Regex.IsMatch(result, @"(?im)^(?:Error\b|Status:\s*(?!ok\b)\S)"))
                throw new InvalidOperationException("Android 启动游戏失败：" + result.Trim());
            var clock = Stopwatch.StartNew();
            while (!InForeground())
            {
                if (clock.Elapsed >= TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("已发出启动命令，20 秒内没有看到游戏进入前台。ADB 返回：" + result.Trim());
                Task.Delay(500, cancellation).GetAwaiter().GetResult();
            }
        }
        processor.AddLog(label + " 第2步完成：" + package + " 已在前台。", changeColor: false);
        return alreadyForeground;
    }

    private static string GamePackage(MaaProcessor processor)
    {
        string resource = processor.ViewModel?.CurrentResource;
        return resource == "官服" ? "com.pinkcore.tkfm"
            : resource == "erolab服务器" ? "com.pinkcore.tkfm.erolabs"
            : throw new InvalidOperationException("无法确定当前服务器对应的游戏包名：" + resource);
    }

    private static string RunCommand(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动进程：" + executable);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string output, error;
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            output = outputTask.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            error = errorTask.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException(Path.GetFileName(executable) + " 命令超过 30 秒未返回");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(executable) + " 执行失败（退出码 " + process.ExitCode
                + "）：" + (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
        return output;
    }

    private static bool EnsureGameRunning(IMaaContext context, MaaProcessor processor)
    {
        var source = processor.CancellationTokenSource
            ?? throw new InvalidOperationException("缺少当前队列取消令牌");
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData("TKFMaa.GamePrepared." + processor.InstanceId), source)) return true;
        try
        {
            var overrides = LoginOverride(context, processor);
            string package = (string)overrides["GameLoginLaunch"]["action"]["param"]["package"];
            // Use the same active configuration as MFA's controller. The persisted value
            // may be an AdbDeviceInfo in memory, so GetValue<JObject> can return an empty default.
            var device = processor.Config.AdbDevice;
            string adb = device.AdbPath, serial = device.AdbSerial;
            if (string.IsNullOrWhiteSpace(adb) || string.IsNullOrWhiteSpace(serial))
                throw new InvalidOperationException("所选 ADB 设备配置不完整："
                    + (string.IsNullOrWhiteSpace(adb) ? "ADB 路径缺失；" : "")
                    + (string.IsNullOrWhiteSpace(serial) ? "设备地址缺失；" : "")
                    + "请重新选择并连接设备");
            // A shell always returns success after emitting a marker, so a transport failure
            // cannot be confused with the ordinary pidof exit status for a stopped game.
            string output = AdbOutput(adb, serial,
                "pidof " + package + "; echo TKFM_PID_CHECK_DONE", source.Token);
            if (!output.Contains("TKFM_PID_CHECK_DONE"))
                throw new InvalidOperationException("ADB 未返回完整游戏进程状态");
            bool running = Regex.IsMatch(output.Replace("TKFM_PID_CHECK_DONE", "").Trim(), @"^\d+(\s+\d+)*$");
            if (!running)
            {
                processor.AddLog("[自动启动] 所选游戏未运行，启动并完成登录。", changeColor: false);
                if (!RunAttempt(context, processor, "GameLoginLaunch",
                    new[] { "GameMainReady" }, overrides.ToString(Formatting.None))) return false;
            }
            else
            {
                // StartApp brings an existing Android task to the foreground without stopping it.
                string activity = AdbOutput(adb, serial, "dumpsys activity activities", source.Token);
                bool foreground = activity.Split('\n').Any(line =>
                    (line.Contains("mResumedActivity") || line.Contains("topResumedActivity"))
                    && Regex.IsMatch(line, @"\b" + Regex.Escape(package) + @"/"));
                if (!foreground)
                {
                    processor.AddLog("[自动启动] 游戏已运行，切回所选游戏。", changeColor: false);
                    var bringForward = (JObject)overrides.DeepClone();
                    bringForward["GameLoginLaunch"]["next"] = new JArray();
                    if (!RunOnce(context, processor, "GameLoginLaunch", bringForward.ToString(Formatting.None))) return false;
                }
                // Let the selected task recognize its current page. Delayed advertisements
                // and other interruptions are handled by the installed recovery branches.
                processor.AddLog("[自动启动] 游戏已运行，直接执行当前任务；广告、公告或返回按钮交由异常恢复处理。",
                    changeColor: false);
            }
            if (Cancelled(context, processor)) return false;
            MarkGamePrepared(processor);
            return true;
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor)) processor.AddLog("[自动启动] " + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static void MarkGamePrepared(MaaProcessor processor)
    {
        if (processor.CancellationTokenSource != null)
            AppDomain.CurrentDomain.SetData("TKFMaa.GamePrepared." + processor.InstanceId, processor.CancellationTokenSource);
    }

    private static string AdbOutput(string adb, string serial, string shell, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(adb)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-s", serial, "shell", shell }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ADB 状态查询"))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            if (Task.WhenAny(exit, Task.Delay(10000, cancellation)).GetAwaiter().GetResult() != exit)
            {
                try { process.Kill(); } catch { }
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("ADB 状态查询超时");
            }
            exit.GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException("ADB 状态查询失败：" + error.GetAwaiter().GetResult().Trim());
            return output.GetAwaiter().GetResult();
        }
    }

    private static bool FinishFailure(MaaProcessor processor, string reason, bool continueAfterFailure)
    {
        processor.AddLog("[异常恢复] " + reason
            + (continueAfterFailure ? " 当前步骤记为失败，继续下一步骤。" : " 停止全部任务。"), "OrangeRed");
        if (!continueAfterFailure)
        {
            // Clear synchronously before returning failure, including on the first run of an old config
            // whose frontend task objects may already have captured ContinueOnError=true.
            processor.TaskQueue.Clear();
        }
        return false; // Never turn a skipped or failed module into a successful result.
    }
}

// Retire saved startup rows and lift the old nested quota into the task's direct option.
// Run only when idle; selecting a resource invalidates the frontend's native tasker.
internal sealed class TkfmGameOptionsMigration : IDisposable
{
    private const string MigrationKey = "TkfmGameOptionsV2";
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<MaaProcessor, JArray> snapshots = new Dictionary<MaaProcessor, JArray>();

    internal TkfmGameOptionsMigration()
    {
        foreach (var processor in MaaProcessor.Processors.ToArray())
            snapshots[processor] = (JArray)processor.InstanceConfiguration.GetValue("TaskItems", new JArray()).DeepClone();
        timer.Tick += Tick;
        timer.Start();
    }

    private void Tick(object sender, EventArgs args)
    {
        foreach (var processor in MaaProcessor.Processors.ToArray())
        {
            if (processor.IsClosed || processor.IsConnecting || processor.IsTaskRunActive
                || processor.ViewModel == null || processor.ViewModel.IsRunning || processor.TaskQueue.Count > 0) continue;
            try
            {
                var tasks = snapshots.TryGetValue(processor, out var saved) ? saved
                    : processor.InstanceConfiguration.GetValue("TaskItems", new JArray());
                if (!processor.InstanceConfiguration.GetValue("TkfmDailyAffairsOptionsV1", false))
                    MigrateDailyAffairsOptions(processor);
                if (processor.InstanceConfiguration.GetValue(MigrationKey, false))
                {
                    snapshots.Remove(processor);
                    continue;
                }
                var startup = tasks.OfType<JObject>().FirstOrDefault(t => (string)t["entry"] == "GameLoginStart");
                var server = (startup?["option"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(o => (string)o["name"] == "选择服务器");
                // Once a new resource is selected explicitly, never replace it with a legacy index.
                string resource = processor.ViewModel.CurrentResource;
                if (resource == "官服" && (int?)server?["index"] == 1)
                {
                    processor.InstanceConfiguration.SetValue(MigrationKey, true);
                    processor.ViewModel.CurrentResource = "erolab服务器";
                    // Finish row migration after resource reload has replaced the task objects.
                    processor.InstanceConfiguration.SetValue(MigrationKey, false);
                    snapshots[processor] = tasks;
                    continue;
                }
                var rows = processor.ViewModel.TaskItemViewModels;
                int recruitmentIndex = 0;
                var oldRecruitments = tasks.OfType<JObject>().Where(t => (string)t["entry"] == "DailyRecruitmentStart").ToArray();
                foreach (var row in rows.Where(t => t.InterfaceItem?.Entry == "DailyRecruitmentStart"))
                {
                    var previous = oldRecruitments.ElementAtOrDefault(recruitmentIndex++);
                    var oldCount = previous?.Descendants().OfType<JObject>()
                        .FirstOrDefault(o => (string)o["name"] == "全境征才循环次数");
                    var options = row.InterfaceItem.Option ??= new List<MaaInterface.MaaInterfaceSelectOption>();
                    options.RemoveAll(o => o.Name == "全境征才立即招募");
                    var count = options.FirstOrDefault(o => o.Name == "全境征才循环次数");
                    if (count == null)
                    {
                        count = new MaaInterface.MaaInterfaceSelectOption { Name = "全境征才循环次数" };
                        options.Add(count);
                    }
                    count.Data ??= new Dictionary<string, string>();
                    string value = (string)oldCount?["data"]?["次数"];
                    if (int.TryParse(value, out var total) && total >= 1 && total <= 100) count.Data["次数"] = value;
                    else if (!count.Data.ContainsKey("次数")) count.Data["次数"] = "4";
                    // Old task-level overrides must not resurrect either retired switch.
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentAfterSlots");
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentCollectResults");
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentCycleGate");
                }
                processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
                    rows.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
                processor.InstanceConfiguration.SetValue(MigrationKey, true);
                snapshots.Remove(processor);
            }
            catch (Exception ex) { LoggerHelper.Warning("TKFMaa 配置迁移暂未完成：" + ex.Message); }
        }
    }

    private static void MigrateDailyAffairsOptions(MaaProcessor processor)
    {
        var names = new[] { "每日政务战争演习", "每日政务情报搜集", "每日政务商业经营" };
        var definitions = MaaProcessor.Interface?.Option;
        if (definitions == null || names.Any(name => !definitions.ContainsKey(name)))
            throw new InvalidOperationException("每日政务选项尚未加载");
        foreach (var row in processor.ViewModel.TaskItemViewModels.Where(t =>
            t.InterfaceItem?.Entry == "DailyStaminaStart" || t.InterfaceItem?.Entry == "CustomStageStart"))
        {
            var options = row.InterfaceItem.Option ??= new List<MaaInterface.MaaInterfaceSelectOption>();
            foreach (string name in names)
                if (!options.Any(option => option.Name == name))
                    options.Add(new MaaInterface.MaaInterfaceSelectOption { Name = name, Index = 0 });
        }
        processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
            processor.ViewModel.TaskItemViewModels.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
        processor.InstanceConfiguration.SetValue("TkfmDailyAffairsOptionsV1", true);
    }

    public void Dispose() { timer.Stop(); timer.Tick -= Tick; snapshots.Clear(); }
}
