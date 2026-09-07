using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace TravelEar;

[BepInPlugin(Guid, Name, VersionString)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "com.sabermage.travelear";
    public const string Name = "TravelEar";
    public const string VersionString = "0.1.0";

    internal static ManualLogSource Logger;
    internal static PluginConfig Settings;

    public override void Load()
    {
        Logger = Log;
        Settings = new PluginConfig(Config);

        Logger.LogInfo($"{Name} {VersionString} loaded. Enabled={Settings.Enabled.Value}");
        // Next: install Harmony patches (Outbound Voice tap), create the Local Voice renderer,
        // and spawn the Helper. See docs/DESIGN.md.
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
