using System.Diagnostics;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Starts the Helper once per game launch. M1 spike version: a plain <see cref="Process.Start(ProcessStartInfo)"/>;
/// the design's out-of-tree spawn (WMI <c>Win32_Process.Create</c>) and the 5 s pipe retry policy
/// belong to <c>REQ-SINK-LIFECYCLE</c>, which stays inactive until M2. A missing or failing Helper
/// only produces a log line (docs/KNOWN-HAZARDS.md 2.1).
/// </summary>
internal static class HelperLauncher
{
    public const string ProcessName = "TravelEar.Helper";

    public static string DefaultPath(string pluginDirectory) =>
        Path.Combine(pluginDirectory, "Helper", ProcessName + ".exe");

    public static void TryLaunch(PluginConfig settings, string pluginDirectory)
    {
        if (!settings.SpawnHelper.Value)
        {
            Plugin.Logger.LogInfo("Sink: SpawnHelper is off; start the Helper by hand.");
            return;
        }

        var path = string.IsNullOrWhiteSpace(settings.HelperPath.Value)
            ? DefaultPath(pluginDirectory)
            : settings.HelperPath.Value;

        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"Sink: Helper not found at '{path}'. Start it by hand or set Sink.HelperPath.");
            return;
        }

        try
        {
            if (Process.GetProcessesByName(ProcessName).Length > 0)
            {
                Plugin.Logger.LogInfo("Sink: Helper is already running.");
                return;
            }

            var options = HelperOptions.Default with { EndpointSetting = settings.SinkEndpoint.Value ?? "" };
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? pluginDirectory,
            };
            foreach (var arg in options.ToArgs()) start.ArgumentList.Add(arg);

            var process = Process.Start(start);
            Plugin.Logger.LogInfo($"Sink: Helper started (pid {process?.Id}) from '{path}' with args [{string.Join(" ", options.ToArgs())}].");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Sink: could not start the Helper: {e.Message}");
        }
    }
}
