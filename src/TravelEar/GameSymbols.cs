using System.Reflection;
using BepInEx.Logging;
using Dissonance;
using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Opus;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Every game class and method the mod touches, resolved once at load. Names are facts from the
/// decompiled game (see AGENTS.md); if any is missing after a game update the whole mod stays
/// off and says so in one log line, instead of running with whatever still binds.
/// </summary>
internal static class GameSymbols
{
    /// <summary><c>OpusEncoder.Encode(ArraySegment&lt;float&gt;, ArraySegment&lt;byte&gt;)</c>: produces every Outbound Voice frame.</summary>
    public static MethodInfo OpusEncoderEncode { get; private set; }

    /// <summary><c>OpusDecoder.Decode(EncodedBuffer, ArraySegment&lt;float&gt;)</c>: the game's own decoder, used for the round trip.</summary>
    public static MethodInfo OpusDecoderDecode { get; private set; }

    /// <summary><c>LocalVoiceProvider.Start()</c>: subscribes the provider to the mic; skipped for the mod-owned instance.</summary>
    public static MethodInfo LocalVoiceProviderStart { get; private set; }

    /// <summary>The provider's <c>IMicrophoneSubscriber.ReceiveMicrophoneData</c> proxy: how decoded PCM enters its ring.</summary>
    public static MethodInfo LocalVoiceProviderReceive { get; private set; }

    /// <summary><c>AudioFilterMixer.OnAudioFilterRead(float[], int)</c>: the end of the Filter Stage; the Tap sits here.</summary>
    public static MethodInfo AudioFilterMixerOnAudioFilterRead { get; private set; }

    /// <summary><c>VoiceBroadcastTrigger.Start()</c>: where the transmit-signal probe collects the scene's triggers.</summary>
    public static MethodInfo VoiceBroadcastTriggerStart { get; private set; }

    public static bool IsBound { get; private set; }

    /// <summary>Unity's SFX Reverb parameter names in its documented order; <c>AudioDynamicReverb</c> exposes them as <c>DSP_*</c>, <c>AudioBasicReverb</c> bare, and the main mixer under the same names.</summary>
    public static readonly string[] ReverbParameterNames =
    {
        "DryLevel", "Room", "RoomHF", "RoomLF", "DecayTime", "DecayHFRatio", "Reflections", "ReflectDelay",
        "Reverb", "ReverbDelay", "HFReference", "LFReference", "Diffusion", "Density",
    };

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Resolves all symbols. Returns false, having logged the complete list of misses, if any
    /// symbol is absent. Callers must not install any hook when this returns false.
    /// </summary>
    public static bool Bind(ManualLogSource log)
    {
        var missing = new SymbolBinder();

        OpusEncoderEncode = Method(missing, typeof(OpusEncoder), "Encode",
            typeof(Il2CppSystem.ArraySegment<float>), typeof(Il2CppSystem.ArraySegment<byte>));
        OpusDecoderDecode = Method(missing, typeof(OpusDecoder), "Decode",
            typeof(EncodedBuffer), typeof(Il2CppSystem.ArraySegment<float>));
        LocalVoiceProviderStart = Method(missing, typeof(LocalVoiceProvider), "Start");
        LocalVoiceProviderReceive = Method(missing, typeof(LocalVoiceProvider),
            "Dissonance_Audio_Capture_IMicrophoneSubscriber_ReceiveMicrophoneData",
            typeof(Il2CppSystem.ArraySegment<float>), typeof(NAudio.Wave.WaveFormat));
        AudioFilterMixerOnAudioFilterRead = Method(missing, typeof(AudioFilterMixer), "OnAudioFilterRead",
            typeof(Il2CppStructArray<float>), typeof(int));
        VoiceBroadcastTriggerStart = Method(missing, typeof(VoiceBroadcastTrigger), "Start");

        // Fields and properties the renderer assigns or reads (interop exposes fields as properties).
        Property(missing, typeof(VoicePlayer), "Cue");
        Property(missing, typeof(VoicePlayer), "LocalVoiceProvider");
        Property(missing, typeof(VoicePlayer), "Volume");
        Property(missing, typeof(VoicePlayer), "PlayerType");
        Property(missing, typeof(VoicePlayer), "Controller");
        Property(missing, typeof(AudioSourceController), "FilterMixer");
        Property(missing, typeof(GlobalAudioEffects), "Instance");
        Property(missing, typeof(GlobalAudioEffects), "VoiceCues");
        Property(missing, typeof(AudioManager), "Instance");
        Property(missing, typeof(AudioManager), "ListenerPosition");
        Property(missing, typeof(LocalVoiceProvider), "CachedVoiceData");
        Property(missing, typeof(LocalVoiceProvider), "CachedVoiceWriteHead");

        // Transmit signal (M2 T0): comms mute, the open room channels and the broadcast triggers.
        Property(missing, typeof(WorldManager), "instance");
        Property(missing, typeof(WorldManager), "dissonanceComms");
        Property(missing, typeof(DissonanceComms), "IsMuted");
        Property(missing, typeof(DissonanceComms), "RoomChannels");
        Property(missing, typeof(RoomChannels), "_openChannelsBySubId");
        Property(missing, typeof(RoomChannel), "_roomId");
        Property(missing, typeof(RoomName), "Name");
        Property(missing, typeof(VoiceBroadcastTrigger), "IsTransmitting");
        Property(missing, typeof(VoiceBroadcastTrigger), "RoomName");
        Property(missing, typeof(VoiceBroadcastTrigger), "Mode");
        Property(missing, typeof(VoiceBroadcastTrigger), "_isVadSpeaking");
        Property(missing, typeof(VoiceBroadcastTrigger), "_activationFaderSettings");
        Property(missing, typeof(VolumeFaderSettings), "_fadeInTicks");
        Property(missing, typeof(VolumeFaderSettings), "_fadeOutTicks");

        // Mixer Stage inputs (M3 T1): the local player's fall state and synced outdoorness, the
        // listener's dynamic reverb (PlayerVoicePlaybackControl.Update's speaker/listener terms).
        Property(missing, typeof(WorldManager), "localPlayerCharacter");
        Property(missing, typeof(PlayerCharacter), "faller");
        Property(missing, typeof(PlayerCharacter), "playerNetworking");
        Property(missing, typeof(PlayerFaller), "isInDanger");
        Property(missing, typeof(PlayerNetworking), "outdoorness");
        Property(missing, typeof(AudioManager), "AudioDynamicReverb");
        Property(missing, typeof(AudioDynamicReverb), "Outdoorness");

        // Megaphone state (M3 T2): the held prop's radio assigner and its broadcast state.
        Property(missing, typeof(PlayerCharacter), "hands");
        Property(missing, typeof(PlayerHands), "heldProp");
        Property(missing, typeof(Prop), "radioVoiceAssigner");
        Property(missing, typeof(RadioVoiceAssigner), "_cachedVoiceType");
        Property(missing, typeof(RadioVoiceAssigner), "isBroadcasting");
        Property(missing, typeof(RadioVoiceAssigner), "latestBroadcastPlayer");
        Property(missing, typeof(RadioVoiceAssigner), "roomName");

        // Environment reverb (M3 T2b): the listener's SFX Reverb parameters as the game writes them
        // (Dynamic or Basic mode), the Master Wet return level and the voice slider.
        Property(missing, typeof(AudioManager), "AudioBasicReverb");
        Property(missing, typeof(AudioDynamicReverb), "Bypass");
        Property(missing, typeof(AudioDynamicReverb), "RoomSize");
        Property(missing, typeof(AudioDynamicReverb), "ReverbTime");
        Property(missing, typeof(AudioDynamicReverb), "Diffusion");
        Property(missing, typeof(AudioBasicReverb), "Bypass");
        foreach (var name in ReverbParameterNames)
        {
            Property(missing, typeof(AudioDynamicReverb), "DSP_" + name);
            Property(missing, typeof(AudioBasicReverb), name);
        }
        Property(missing, typeof(GlobalAudioEffects), "Mixer");
        Property(missing, typeof(GlobalAudioEffects), "VoiceNormalVol");
        Property(missing, typeof(GlobalAudioEffects), "VoiceAudioSettingsVol");

        // One verdict, one line: with any miss the mod stays off (docs/KNOWN-HAZARDS.md 3.1).
        IsBound = missing.Complete(out var report);
        if (IsBound) log.LogInfo(report);
        else log.LogError(report);
        return IsBound;
    }

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // No lambdas here: a lambda returning a BCL reference type makes the compiler synthesize
    // NullableAttribute, which clashes with the Il2CppInterop proxies (see the csproj).
    private static MethodInfo Method(SymbolBinder binder, Type type, string name, params Type[] parameters)
    {
        MethodInfo method = null;
        try { method = type.GetMethod(name, Any, null, parameters, null); }
        catch (Exception) { /* ambiguous or unresolvable: a miss, never fatal here */ }
        binder.Require($"{type.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})", method != null);
        return method;
    }

    private static void Property(SymbolBinder binder, Type type, string name)
    {
        PropertyInfo property = null;
        try { property = type.GetProperty(name, Any); }
        catch (Exception) { /* ambiguous: a miss */ }
        binder.Require($"{type.FullName}.{name}", property != null);
    }
}
