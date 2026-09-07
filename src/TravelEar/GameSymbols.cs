using System.Reflection;
using BepInEx.Logging;
using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Opus;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

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

    public static bool IsBound { get; private set; }

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Resolves all symbols. Returns false, having logged the complete list of misses, if any
    /// symbol is absent. Callers must not install any hook when this returns false.
    /// </summary>
    public static bool Bind(ManualLogSource log)
    {
        var missing = new List<string>();

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

        if (missing.Count > 0)
        {
            IsBound = false;
            log.LogError($"TravelEar disabled: {missing.Count} game symbol(s) not found after a game update: {string.Join(", ", missing)}");
            return false;
        }

        IsBound = true;
        log.LogInfo("Game symbols bound.");
        return true;
    }

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static MethodInfo Method(List<string> missing, Type type, string name, params Type[] parameters)
    {
        MethodInfo method = null;
        try
        {
            method = type.GetMethod(name, Any, null, parameters, null);
        }
        catch (Exception)
        {
            // Ambiguous or otherwise unresolvable: treated as missing below.
        }
        if (method is null)
            missing.Add($"{type.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
        return method;
    }

    private static void Property(List<string> missing, Type type, string name)
    {
        PropertyInfo property = null;
        try
        {
            property = type.GetProperty(name, Any);
        }
        catch (Exception)
        {
            // Ambiguous: treated as missing below.
        }
        if (property is null)
            missing.Add($"{type.FullName}.{name}");
    }
}
