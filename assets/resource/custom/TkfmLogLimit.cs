using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;

// Keep the native log view, collection binding and scrolling.
public sealed class TkfmLogLimit : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmLogLimit";

    public TkfmLogLimit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.LogLimit.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmLogLimitService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmLogLimitService : IDisposable
{
    private const int Limit = 100;
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<MaaProcessor, TkfmRecognitionFailureLog> diagnostics =
        new Dictionary<MaaProcessor, TkfmRecognitionFailureLog>();
    private bool disposed;
    private bool errorReported;

    internal TkfmLogLimitService()
    {
        timer.Tick += Trim;
        timer.Start();
        Trim(null, EventArgs.Empty);
    }

    private void Trim(object sender, EventArgs args)
    {
        if (disposed) return;
        var processors = MaaProcessor.Processors.ToArray();
        foreach (var pair in diagnostics.ToArray())
        {
            if (processors.Contains(pair.Key) && pair.Value.IsAttachedTo(pair.Key.MaaTasker)) continue;
            pair.Value.Dispose();
            diagnostics.Remove(pair.Key);
        }
        foreach (var processor in processors)
        {
            try
            {
                if (processor.MaaTasker is MaaTasker tasker && !diagnostics.ContainsKey(processor))
                    diagnostics.Add(processor, new TkfmRecognitionFailureLog(processor, tasker));
                int excess = processor.LogItemViewModels.Count - Limit;
                // The native disposable collection owns removed rows. Do not
                // mutate it from CollectionChanged or issue any scroll requests.
                if (excess > 0) processor.LogItemViewModels.RemoveRange(0, excess);
            }
            catch (Exception ex)
            {
                if (errorReported) continue;
                errorReported = true;
                LoggerHelper.Warning("TKFMaa 日志条数限制未完成：" + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Trim;
        foreach (var diagnostic in diagnostics.Values) diagnostic.Dispose();
        diagnostics.Clear();
    }
}

internal sealed class TkfmRecognitionFailureLog : IDisposable
{
    private const int MaxTargetsPerRound = 48;
    private const int MaxPreviews = 8;
    private readonly MaaProcessor processor;
    private readonly MaaTasker tasker;
    private readonly object sync = new object();
    private readonly Dictionary<IntPtr, HashSet<string>> rounds = new Dictionary<IntPtr, HashSet<string>>();
    private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
    private volatile bool disposed;
    private bool errorReported;

    internal TkfmRecognitionFailureLog(MaaProcessor processor, MaaTasker tasker)
    {
        this.processor = processor;
        this.tasker = tasker;
        tasker.Callback += OnCallback;
    }

    internal bool IsAttachedTo(MaaTasker? current) => ReferenceEquals(tasker, current);

    private void OnCallback(object? sender, MaaCallbackEventArgs args)
    {
        if (disposed) return;
        try
        {
            if (args.Message is "Tasker.Task.Starting" or "Tasker.Task.Succeeded" or "Tasker.Task.Failed")
            {
                lock (sync)
                {
                    rounds.Clear();
                    reported.Clear();
                }
                return;
            }
            if (!(sender is MaaContext context)) return;
            switch (args.Message)
            {
                case "Node.NextList.Starting":
                case "Node.PipelineNode.Succeeded":
                    lock (sync) rounds.Remove(context.Handle);
                    break;
                case "Node.Recognition.Failed":
                case "Node.RecognitionNode.Failed":
                    RememberTarget(context.Handle, args.Details);
                    break;
                case "Node.PipelineNode.Failed":
                    ReportTimeout(context, args.Details);
                    break;
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void RememberTarget(IntPtr handle, string details)
    {
        if (!details.Contains("\"algorithm\":\"TemplateMatch\"", StringComparison.Ordinal)
            && !details.Contains("\"algorithm\":\"FeatureMatch\"", StringComparison.Ordinal)) return;
        var data = JObject.Parse(details);
        if (data["reco_details"]?["algorithm"]?.Value<string>() is not ("TemplateMatch" or "FeatureMatch")) return;
        var name = (string?)data["name"];
        if (string.IsNullOrEmpty(name)) return;
        lock (sync)
        {
            if (!rounds.TryGetValue(handle, out var names))
                rounds.Add(handle, names = new HashSet<string>(StringComparer.Ordinal));
            if (names.Count < MaxTargetsPerRound) names.Add(name);
        }
    }

    private void ReportTimeout(MaaContext context, string details)
    {
        HashSet<string>? names;
        lock (sync)
        {
            if (!rounds.Remove(context.Handle, out names)) return;
        }
        // A failed action has node details. A timeout waiting for the next recognition does not.
        var data = JObject.Parse(details);
        if (data["node_details"] is JObject || processor.CancellationTokenSource?.IsCancellationRequested == true)
            return;
        var node = (string?)data["name"] ?? "未知节点";
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            // The native context is only valid inside this callback. Read the active
            // node data here so task-specific overrides and user templates are included.
            if (!context.GetNodeData(name, out var raw)) continue;
            var recognition = JObject.Parse(raw)["recognition"];
            if (recognition?["type"]?.Value<string>() is not ("TemplateMatch" or "FeatureMatch")) continue;
            var template = recognition["param"]?["template"];
            if (template is JArray array)
            {
                foreach (var item in array.Values<string>())
                    if (!string.IsNullOrWhiteSpace(item)) templates.Add(item);
            }
            else if (template?.Type == JTokenType.String && template.Value<string>() is { Length: > 0 } path)
                templates.Add(path);
        }
        if (templates.Count == 0) return;
        var paths = templates.ToArray();
        var key = node + "\n" + string.Join("\n", paths.OrderBy(path => path, StringComparer.Ordinal));
        lock (sync)
        {
            if (!reported.Add(key)) return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            try { WriteLog(node, paths); }
            catch (Exception ex) { ReportError(ex); }
        });
    }

    private void WriteLog(string node, string[] templates)
    {
        if (disposed) return;
        var current = processor.ViewModel?.CurrentResources
            .FirstOrDefault(resource => resource.Name == processor.ViewModel.CurrentResource);
        var roots = (current?.ResolvedPath ?? current?.Path ?? new List<string> { AppPaths.ResourceDirectory })
            .Prepend(Path.Combine(AppPaths.InstallRoot, "userdata", "resource"))
            .Select(root => Path.GetFullPath(Path.Combine(root, "image")) + Path.DirectorySeparatorChar)
            .ToArray();
        var message = new StringBuilder("在寻找以下图片时失败。节点 `").Append(node).Append("`。");
        int previews = 0;
        foreach (var template in templates)
        {
            message.Append("\n\n- <code>").Append(WebUtility.HtmlEncode(template)).Append("</code>");
            if (previews >= MaxPreviews) continue;
            var path = FindImage(roots, template);
            if (path == null) continue;
            message.Append(" <img width=\"128\" src=\"")
                .Append(WebUtility.HtmlEncode(path.Replace('\\', '/'))).Append("\" />");
            previews++;
        }
        if (templates.Length > MaxPreviews)
            message.Append("\n\n每条日志最多显示 ").Append(MaxPreviews).Append(" 张预览图。");
        processor.AddLog(message.ToString(), "OrangeRed", changeColor: false, useMarkdown: true);
    }

    private void ReportError(Exception error)
    {
        lock (sync)
        {
            if (errorReported) return;
            errorReported = true;
        }
        LoggerHelper.Warning("识别失败日志未能生成：" + error.Message);
    }

    private static string? FindImage(string[] roots, string template)
    {
        if (Path.IsPathRooted(template) || template.Contains(':')) return null;
        foreach (var root in roots)
        {
            var path = Path.GetFullPath(Path.Combine(root, template));
            if (path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && File.Exists(path)) return path;
        }
        return null;
    }

    public void Dispose()
    {
        disposed = true;
        tasker.Callback -= OnCallback;
    }
}
