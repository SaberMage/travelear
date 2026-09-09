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
    private OffsetRow _offsetRow;
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
            _harmony.PatchAll(typeof(MixerFloats));
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
        // The feed point (ADR-0005) picks the ring: the encoder thread's (mono, decoder rate) or the Tap's.
        var feed = Settings.SinkFeed.Value;
        _pump = feed == SinkFeedPoint.Encoder
            ? new SinkPump(SinkFeed.Ring, SinkFeed.Stamps, () => SinkFeed.Channels, () => LocalVoiceDecoder.SampleRate, () => Settings.Downmix.Value)
            : new SinkPump(TapFilter.Ring, TapFilter.SinkStamps, () => TapFilter.Channels, () => LocalVoiceRenderer.SampleRate, () => Settings.Downmix.Value);
        _pump.Start();
        Logger.LogInfo($"Sink feed point: {feed}.");
        _offset = new OffsetMonitor(Logger, HelperOptions.Default.BackPipe);
        _offset.Start();
        _offsetRow = new OffsetRow(Logger, () => _offset.LastAverageMs);
        HelperLauncher.TryLaunch(Settings, Paths.BepInExRootPath);

        // Renderer: built lazily from the main thread once the game's audio system exists.
        var mixer = new MixerStageToggles(Settings.MixerStage.Value, Settings.MixerDry.Value, Settings.MixerHigh.Value, Settings.MixerReverbFall.Value, Settings.MixerReverbBoost.Value);
        var megaphone = new MegaphoneToggles(Settings.MegaphoneVoice.Value, Settings.MegaphoneCrusher.Value, Settings.MegaphoneHighPass.Value, Settings.MegaphoneCompressors.Value);
        var environment = new EnvironmentReverbToggles(Settings.EnvironmentReverb.Value, Settings.EnvironmentReverbDryCopy.Value, Settings.EnvironmentReverbBusGains.Value, Settings.EnvironmentReverbVoiceSlider.Value);
        _renderer = new LocalVoiceRenderer(Logger, _pump, feed, Settings.SelfEarForwardMeters.Value, Settings.TransmitGate.Value, Settings.TransmitFadeOutMs.Value, Settings.ReadHeadMarginFrames.Value, Settings.SelfEarEqDryWet.Value, mixer, Settings.ReverbDecaySeconds.Value,
            megaphone, Settings.MegaphoneMix.Value, environment);
        ClassInjector.RegisterTypeInIl2Cpp<TravelEarBehaviour>();
        _driver = new GameObject("TravelEar") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(_driver);
        _driver.AddComponent<TravelEarBehaviour>();
        Logger.LogInfo("Local Voice renderer armed; waiting for the audio system.");
    }

    public override bool Unload()
    {
        _offsetRow?.Dispose();
        _offset?.Dispose();
        _pump?.Dispose();
        _harmony?.UnpatchSelf();
        _harmony = null;
        return true;
    }
}

/// <summary>Where the Sink is fed from (ADR-0005).</summary>
public enum SinkFeedPoint
{
    /// <summary>The encoder thread: decoded Outbound Voice, remote-path processing, then straight to the Sink. Default.</summary>
    Encoder,
    /// <summary>The Tap on Unity's audio thread behind a game <c>VoicePlayer</c> (ADR-0003; M1-M2 path, kept for A/B).</summary>
    VoicePlayer,
}

/// <summary>How the Megaphone voice combines with the direct voice (config <c>Fidelity.MegaphoneMix</c>).</summary>
public enum MegaphoneMix
{
    /// <summary>Megaphone output added to the direct voice: what a listener beside the holder hears.</summary>
    Add,
    /// <summary>Only the megaphone output while broadcasting.</summary>
    Replace,
}

/// <summary>User-facing settings. Every entry is surfaced automatically by ModSettingsMenu if installed.</summary>
internal sealed class PluginConfig
{
    public ConfigEntry<SinkFeedPoint> SinkFeed { get; }
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> SpawnHelper { get; }
    public ConfigEntry<string> HelperPath { get; }
    public ConfigEntry<string> SinkEndpoint { get; }
    public ConfigEntry<bool> MixerStage { get; }
    public ConfigEntry<bool> MixerDry { get; }
    public ConfigEntry<bool> MixerHigh { get; }
    public ConfigEntry<bool> MixerReverbFall { get; }
    public ConfigEntry<bool> MixerReverbBoost { get; }
    public ConfigEntry<float> ReverbDecaySeconds { get; }
    public ConfigEntry<bool> MegaphoneVoice { get; }
    public ConfigEntry<bool> MegaphoneCrusher { get; }
    public ConfigEntry<bool> MegaphoneHighPass { get; }
    public ConfigEntry<bool> MegaphoneCompressors { get; }
    public ConfigEntry<MegaphoneMix> MegaphoneMix { get; }
    public ConfigEntry<bool> EnvironmentReverb { get; }
    public ConfigEntry<bool> EnvironmentReverbDryCopy { get; }
    public ConfigEntry<bool> EnvironmentReverbBusGains { get; }
    public ConfigEntry<bool> EnvironmentReverbVoiceSlider { get; }
    public ConfigEntry<bool> TransmitGate { get; }
    public ConfigEntry<float> TransmitFadeOutMs { get; }
    public ConfigEntry<bool> Downmix { get; }
    public ConfigEntry<float> ReadHeadMarginFrames { get; }
    public ConfigEntry<float> SelfEarForwardMeters { get; }
    public ConfigEntry<float> SelfEarEqDryWet { get; }

    // [impl->REQ-CONFIG-BEPINEX]
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
        SinkFeed = file.Bind("Fidelity", "SinkFeed", SinkFeedPoint.Encoder,
            "Where Local Voice is taken from. Encoder = the decoded outbound voice on the game's mic thread, processed by the mod and sent straight to the Sink (lowest offset, immune to the game's audio-thread stalls). VoicePlayer = the M1-M2 path through an in-game VoicePlayer and the Tap on Unity's audio thread (for A/B only).");
        // [impl->REQ-MIXER-RESYNTH]
        MixerStage = file.Bind("Fidelity", "MixerStage", true,
            "Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character). Master switch for the Mixer* toggles below.");
        MixerDry = file.Bind("Fidelity", "MixerDry", true,
            "Apply the game's per-voice dry level (Dry{n}). At your own ears it is 0 dB, so this only matters for A/B.");
        MixerHigh = file.Bind("Fidelity", "MixerHigh", true,
            "Apply the game's occlusion high cut (High{n}) as a 3 kHz high shelf. 0 dB at your own ears (nothing between you and yourself).");
        MixerReverbFall = file.Bind("Fidelity", "MixerReverbFall", true,
            "Apply the fall reverb send (ReverbFallWet{n}): the reverb other players hear on your voice while you are falling outdoors. Approximate reverb.");
        MixerReverbBoost = file.Bind("Fidelity", "MixerReverbBoost", true,
            "Apply the reverb boost send (ReverbBoostWet{n}): zero at your own ears by the game's formula; kept for A/B.");
        ReverbDecaySeconds = file.Bind("Fidelity", "ReverbDecaySeconds", 1.5f,
            "Decay time (RT60) of the approximate reverb behind the sends, in seconds. Tune by ear against a second-client recording.");
        // [impl->REQ-RENDER-MEGAPHONE]
        MegaphoneVoice = file.Bind("Fidelity", "MegaphoneVoice", true,
            "Render the megaphone's output while you hold and use one: the game's bit-crusher and 300 Hz high-pass, then its two mixer compressors, as a listener beside you hears it. Master switch for the Megaphone* toggles.");
        MegaphoneCrusher = file.Bind("Fidelity", "MegaphoneCrusher", true,
            "The megaphone's sample-hold crusher (4000 Hz, half wet, half smoothed). Exact port.");
        MegaphoneHighPass = file.Bind("Fidelity", "MegaphoneHighPass", true,
            "The megaphone's 300 Hz high-pass. Exact port.");
        MegaphoneCompressors = file.Bind("Fidelity", "MegaphoneCompressors", true,
            "The megaphone mixer's compressor (-25 dB, +6 dB make-up) and post-compressor (-15 dB). Approximate.");
        MegaphoneMix = file.Bind("Fidelity", "MegaphoneMix", TravelEar.MegaphoneMix.Add,
            "Add = the megaphone output on top of your direct voice, as a listener beside you hears both. Replace = only the megaphone output while broadcasting.");
        // [impl->REQ-MIXER-RESYNTH]
        EnvironmentReverb = file.Bind("Fidelity", "EnvironmentReverb", true,
            "Apply the room reverb a listener beside you hears on your voice (the game's dynamic reverb: hallways, caves, outdoors), from the same live parameters the game writes each frame. Master switch for the EnvironmentReverb* toggles. Approximate reverb, exact levels and decay.");
        EnvironmentReverbDryCopy = file.Bind("Fidelity", "EnvironmentReverbDryCopy", true,
            "The reverb's own un-reverbed copy of the voice (its DryLevel), which the game mixes on top of the direct path. It is what makes a nearby voice sit in the room rather than beside it.");
        EnvironmentReverbBusGains = file.Bind("Fidelity", "EnvironmentReverbBusGains", true,
            "Apply the fixed bus trims a voice meets on a listener's machine (-3 dB voice group, -6 dB dry bus). Off = both at 0 dB, louder than the game.");
        EnvironmentReverbVoiceSlider = file.Bind("Fidelity", "EnvironmentReverbVoiceSlider", false,
            "Multiply by the listener's voice volume slider as the game does. Off by default: the Sink level convention already follows your own slider.");
        TransmitGate = file.Bind("Fidelity", "TransmitGate", true,
            "Render Local Voice only while peers receive it (a voice-activation or push-to-talk channel is open); silence otherwise. Off = render everything the mic encodes, noise floor included.");
        TransmitFadeOutMs = file.Bind("Fidelity", "TransmitFadeOutMs", 0f,
            "Fade-out of Local Voice when the game's voice activation stops hearing you, in ms. 0 = the game's own channel fade (read from its voice-activation trigger, logged as 'Transmit fade:'). Raise it if speech still chops between words.");
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
