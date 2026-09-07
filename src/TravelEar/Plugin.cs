using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using TravelEar.Core;
using UnityEngine;
using Object = UnityEngine.Object;

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
    private SinkPump _pump;
    private OffsetMonitor _offset;
    private LocalVoiceRenderer _renderer;
    private GameObject _driver;

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
            Logger.LogInfo("Outbound Voice tap installed on OpusEncoder.Encode.");
            _harmony.PatchAll(typeof(RoundTripProvider));
            _harmony.PatchAll(typeof(TapFilter));
            Logger.LogInfo("Round-trip provider guard and Tap installed.");
            _harmony.PatchAll(typeof(TransmitSignal));
            Logger.LogInfo("Transmit signal probe installed on VoiceBroadcastTrigger.Start.");
        }
        catch (Exception e)
        {
            Logger.LogError($"TravelEar disabled: failed to install hooks: {e}");
            _harmony?.UnpatchSelf();
            _harmony = null;
            return;
        }

        // Sink side first: the pump only ever waits for a Helper, so it can never block the game.
        _pump = new SinkPump(TapFilter.Ring, () => TapFilter.Channels, () => LocalVoiceRenderer.SampleRate, () => Settings.Downmix.Value);
        _pump.Start();
        _offset = new OffsetMonitor(Logger, HelperOptions.Default.BackPipe);
        _offset.Start();
        HelperLauncher.TryLaunch(Settings, Paths.BepInExRootPath);

        // Renderer: built lazily from the main thread once the game's audio system exists.
        _renderer = new LocalVoiceRenderer(Logger, _pump, Settings.SelfEarForwardMeters.Value, Settings.TransmitGate.Value, Settings.ReadHeadMarginFrames.Value, Settings.SelfEarEqDryWet.Value);
        ClassInjector.RegisterTypeInIl2Cpp<TravelEarBehaviour>();
        _driver = new GameObject("TravelEar") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(_driver);
        _driver.AddComponent<TravelEarBehaviour>();
        Logger.LogInfo("Local Voice renderer armed; waiting for the audio system.");
    }

    public override bool Unload()
    {
        _offset?.Dispose();
        _pump?.Dispose();
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
    public ConfigEntry<string> HelperPath { get; }
    public ConfigEntry<string> SinkEndpoint { get; }
    public ConfigEntry<bool> MixerStage { get; }
    public ConfigEntry<bool> TransmitGate { get; }
    public ConfigEntry<bool> Downmix { get; }
    public ConfigEntry<float> ReadHeadMarginFrames { get; }
    public ConfigEntry<float> SelfEarForwardMeters { get; }
    public ConfigEntry<float> SelfEarEqDryWet { get; }

    public PluginConfig(ConfigFile file)
    {
        Enabled = file.Bind("General", "Enabled", true,
            "Render Local Voice and stream it to the Sink.");
        SpawnHelper = file.Bind("Sink", "SpawnHelper", true,
            "Launch the TravelEar Helper process automatically when the game starts.");
        HelperPath = file.Bind("Sink", "HelperPath", "",
            @"Full path to TravelEar.Helper.exe. Empty = BepInEx\TravelEar.Helper\TravelEar.Helper.exe.");
        SinkEndpoint = file.Bind("Sink", "SinkEndpoint", "",
            "Substring of the Windows playback device the Helper renders to. Empty = system default device.");
        MixerStage = file.Bind("Fidelity", "MixerStage", true,
            "Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character).");
        TransmitGate = file.Bind("Fidelity", "TransmitGate", true,
            "Render Local Voice only while peers receive it (a voice-activation or push-to-talk channel is open); silence otherwise. Off = render everything the mic encodes, noise floor included.");
        Downmix = file.Bind("Sink", "Downmix", false,
            "Downmix Local Voice to mono before sending it to the Sink.");
        // [impl->REQ-OFFSET-MEASURE]
        ReadHeadMarginFrames = file.Bind("Fidelity", "ReadHeadMarginFrames", 1.5f,
            "How far behind the provider's write head the Local Voice read head is placed at each talk burst, in 60 ms frames. Lower = less Offset but more read-head resyncs (see the 'Local Voice stats' log line); raise it if resyncs climb.");
        SelfEarForwardMeters = file.Bind("Ear", "SelfEarForwardMeters", 0.0762f,
            "How far in front of the listener the Local Voice emitter sits, in metres (0.0762 = 3 in). 0 puts it on the listener, which pans oddly.");
        // [impl->REQ-EAR-SELF]
        SelfEarEqDryWet = file.Bind("Fidelity", "SelfEarEqDryWet", 0f,
            "Wet mix (0-1) of the game's 400 Hz voice EQ on Local Voice. Remote voices fade it in with distance and angle; at the Self-Ear both are zero, so 0 = the dry voice a listener next to you hears. Raise it to hear the through-a-wall character.");
    }
}
