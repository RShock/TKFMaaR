using System.Reflection;
using System.Text.Json.Nodes;

// MFA normally loads resource extensions only after the controller connects.
// The scheduler must already be available when the saved emulator is off.
internal static class ScheduleBootstrap
{
    internal static void Start(string root) => _ = Task.Run(async () =>
    {
        const string serviceKey = "TKFMaa.Dispatch.Schedule.Service";
        try
        {
            var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "interface.json")));
            var directories = (definition?["resource"] as JsonArray)?.OfType<JsonObject>()
                .SelectMany(resource => (resource["path"] as JsonArray)?.Select(path => path?.ToString()) ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(Path.Combine(root, path!, "custom")));
            string? directory = directories?.FirstOrDefault(path => File.Exists(Path.Combine(path, "TkfmDispatchSchedule.cs")));
            if (directory == null) return;

            for (int attempt = 0; attempt < 180; attempt++)
            {
                if (AppDomain.CurrentDomain.GetData(serviceKey) != null) return;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var core = assemblies.FirstOrDefault(assembly => assembly.GetName().Name == "MFAAvalonia.Core");
                var instances = core?.GetType("MFAAvalonia.Helper.Instances");
                var dispatcher = assemblies.Select(assembly => assembly.GetType("Avalonia.Threading.Dispatcher"))
                    .FirstOrDefault(type => type != null);
                if (instances?.GetProperty("TopLevel")?.GetValue(null) != null && dispatcher != null)
                {
                    var ui = dispatcher.GetProperty("UIThread")!.GetValue(null);
                    var post = dispatcher.GetMethods().First(method => method.Name == "Post"
                        && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(Action));
                    object? priority = post.GetParameters()[1].DefaultValue;
                    priority ??= Activator.CreateInstance(post.GetParameters()[1].ParameterType);
                    post.Invoke(ui, new[] { (object)(Action)(() =>
                    {
                        try
                        {
                            if (AppDomain.CurrentDomain.GetData(serviceKey) != null) return;
                            _ = typeof(System.Net.Sockets.TcpClient).Assembly;
                            if (OperatingSystem.IsWindows()) _ = typeof(Microsoft.Win32.Registry).Assembly;
                            core!.GetType("MFAAvalonia.Extensions.MaaFW.CustomClassLoader", true)!
                                .GetMethod("GetCustomClasses")!.Invoke(null,
                                    new object[] { directory, new[] { "IMaaCustomRecognition", "IMaaCustomAction" } });
                        }
                        catch (Exception ex) { Record(root, ex); }
                    }), priority });
                    return;
                }
                await Task.Delay(500);
            }
        }
        catch (Exception ex) { Record(root, ex); }
    });

    private static void Record(string root, Exception error)
    {
        try
        {
            string logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "tkfm-schedule-bootstrap.log"),
                $"{DateTimeOffset.Now:O} {error.GetBaseException().Message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
