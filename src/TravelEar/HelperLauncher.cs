using System.Diagnostics;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Starts the Helper once per game launch (<c>REQ-SINK-LIFECYCLE</c>). The policy (spawn once,
/// never respawn) lives in <see cref="HelperLifecycle"/>; this class only observes (config, file,
/// running processes) and acts. The Helper is launched with <c>--detach</c>, so the process that
/// stays up is a child of a launcher that exits at once, not of the game: OBS's process-tree
/// matching then cannot fold the Helper's audio into the game's capture. A missing or failing
/// Helper only produces a log line (docs/KNOWN-HAZARDS.md 2.1).
/// </summary>
internal static class HelperLauncher
{
    public const string ProcessName = "TravelEar.Helper";

    private static readonly HelperLifecycle Lifecycle = new();

    /// <summary>
    /// <c>BepInEx\TravelEar.Helper\TravelEar.Helper.exe</c>: beside, not inside, the plugins folder,
    /// because BepInEx examines every DLL under <c>plugins</c> as a plugin candidate.
    /// </summary>
    public static string DefaultPath(string bepInExRoot) =>
        Path.Combine(bepInExRoot, "TravelEar.Helper", ProcessName + ".exe");

    // [impl->REQ-SINK-LIFECYCLE]
    public static void TryLaunch(PluginConfig settings, string bepInExRoot)
    {
        var path = string.IsNullOrWhiteSpace(settings.HelperPath.Value)
            ? DefaultPath(bepInExRoot)
            : settings.HelperPath.Value;

        bool alreadyRunning;
        try
        {
            alreadyRunning = Process.GetProcessesByName(ProcessName).Length > 0;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Sink: could not list processes ({e.Message}); assuming no Helper is running.");
            alreadyRunning = false;
        }

        var decision = Lifecycle.DecideSpawn(settings.SpawnHelper.Value, File.Exists(path), alreadyRunning);
        switch (decision)
        {
            case SpawnDecision.SkipDisabled:
                Plugin.Logger.LogInfo("Sink: SpawnHelper is off; start the Helper by hand.");
                return;
            case SpawnDecision.SkipMissing:
                Plugin.Logger.LogWarning($"Sink: Helper not found at '{path}'. Start it by hand or set Sink.HelperPath.");
                return;
            case SpawnDecision.SkipAlreadyRunning:
                Plugin.Logger.LogInfo("Sink: Helper is already running.");
                return;
            case SpawnDecision.SkipAlreadySpawned:
                Plugin.Logger.LogInfo("Sink: Helper was already spawned this session; not respawning.");
                return;
        }

        try
        {
            var options = HelperOptions.Default with { EndpointSetting = settings.SinkEndpoint.Value ?? "", Detach = true };
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? bepInExRoot,
            };
            foreach (var arg in options.ToArgs()) start.ArgumentList.Add(arg);

            using var launcher = Process.Start(start);
            Plugin.Logger.LogInfo($"Sink: Helper launcher started (pid {launcher?.Id}) from '{path}' with args [{string.Join(" ", options.ToArgs())}]; the Helper detaches from it.");
        }
        catch (Exception e)
        {
            // [impl->REQ-HAZARD-NO-GAMEPLAY-IMPACT]
            Lifecycle.RecordSpawnFailure();
            Plugin.Logger.LogWarning($"Sink: could not start the Helper: {e.Message}. Start it by hand; the mod will not retry.");
        }
    }
}
