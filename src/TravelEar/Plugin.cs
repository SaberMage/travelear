using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TravelEar;

[BepInPlugin(Guid, Name, VersionString)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "com.sabermage.travelear";
    public const string Name = "TravelEar";
    public const string VersionString = "0.1.0";

    internal static ManualLogSource Logger;
    internal static PluginConfig Settings;

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Settings = new PluginConfig(Config);
        Logger.LogInfo($"{Name} {VersionString} loaded. Enabled={Settings.Enabled.Value}");

        if (!Settings.Enabled.Value)
        {
            Logger.LogInfo("Disabled by config; no hooks installed.");
            return;
        }

        // Hard-fail bind: any missing game symbol means no hooks at all (docs/KNOWN-HAZARDS.md).
        if (!GameSymbols.Bind(Logger))
            return;

        try
        {
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(OutboundVoiceTap));
            Logger.LogInfo("Outbound Voice tap installed on MirrorIgnoranceClient.SendUnreliable.");
            _harmony.PatchAll(typeof(EncodedVoiceTap));
            Logger.LogInfo("Encoded Voice tap installed on BaseClient.SendVoiceData.");
            SpikeCanary.PatchAll(_harmony);
            Logger.LogInfo("Spike canaries installed (SendReliable, Send, PreprocessPacketToServer).");
        }
        catch (Exception e)
        {
            Logger.LogError($"TravelEar disabled: failed to install the Outbound Voice tap: {e}");
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
        // Next (M1 T3): decode tapped frames, feed a mod-owned VoicePlayer, Tap filter, Sink pipe, Helper.
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        return true;
    }
}

/// <summary>User-facing settings. Every entry is surfaced automatically by ModSettingsMenu if installed.</summary>
internal sealed class PluginConfig
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> SpawnHelper { get; }
    public ConfigEntry<string> SinkEndpoint { get; }
    public ConfigEntry<bool> MixerStage { get; }
    public ConfigEntry<bool> Downmix { get; }

    public PluginConfig(ConfigFile file)
    {
        Enabled = file.Bind("General", "Enabled", true,
            "Render Local Voice and stream it to the Sink.");
        SpawnHelper = file.Bind("Sink", "SpawnHelper", true,
            "Launch the TravelEar Helper process automatically when the game starts.");
        SinkEndpoint = file.Bind("Sink", "SinkEndpoint", "",
            "Substring of the Windows playback device the Helper renders to. Empty = system default device.");
        MixerStage = file.Bind("Fidelity", "MixerStage", true,
            "Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character).");
        Downmix = file.Bind("Sink", "Downmix", false,
            "Downmix Local Voice to mono before sending it to the Sink.");
    }
}
