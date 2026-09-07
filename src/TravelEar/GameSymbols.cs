using System.Reflection;
using BepInEx.Logging;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Opus;
using Dissonance.Integrations.MirrorIgnorance;
using Dissonance.Networking;

namespace TravelEar;

/// <summary>
/// Every game class and method the mod touches, resolved once at load. Names are facts from the
/// decompiled game (see AGENTS.md); if any is missing after a game update the whole mod stays
/// off and says so in one log line, instead of running with whatever still binds.
/// </summary>
internal static class GameSymbols
{
    /// <summary><c>MirrorIgnoranceClient.SendUnreliable(ArraySegment&lt;byte&gt;)</c>: every Outbound Voice packet passes through here.</summary>
    public static MethodInfo MirrorClientSendUnreliable { get; private set; }

    /// <summary><c>BaseClient&lt;...&gt;.SendVoiceData(ArraySegment&lt;byte&gt;)</c>: every encoded Opus frame while transmitting, peers or not.</summary>
    public static MethodInfo BaseClientSendVoiceData { get; private set; }

    /// <summary><c>OpusEncoder.Encode(ArraySegment&lt;float&gt;, ArraySegment&lt;byte&gt;)</c>: the codec call that produces Outbound Voice bytes.</summary>
    public static MethodInfo OpusEncoderEncode { get; private set; }
    public static MethodInfo EncoderPipelineEncodeFrames { get; private set; }

    /// <summary>Spike S2 canaries (see <see cref="SpikeCanary"/>).</summary>
    public static MethodInfo MirrorClientSendReliable { get; private set; }
    public static MethodInfo MirrorClientSend { get; private set; }
    public static MethodInfo CommsNetworkPreprocessPacketToServer { get; private set; }

    /// <summary>Spike S2 trigger probe (see <see cref="SpikeTriggerProbe"/>).</summary>
    public static MethodInfo TriggerStart { get; private set; }
    public static MethodInfo TriggerUpdate { get; private set; }
    public static MethodInfo TriggerOpenChannel { get; private set; }
    public static MethodInfo TriggerCloseChannel { get; private set; }

    public static bool IsBound { get; private set; }

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Resolves all symbols. Returns false, having logged the complete list of misses, if any
    /// symbol is absent. Callers must not install any hook when this returns false.
    /// </summary>
    public static bool Bind(ManualLogSource log)
    {
        var missing = new List<string>();

        MirrorClientSendUnreliable = Method(missing, typeof(MirrorIgnoranceClient), "SendUnreliable",
            typeof(Il2CppSystem.ArraySegment<byte>));
        BaseClientSendVoiceData = Method(missing, typeof(BaseClient<MirrorIgnoranceServer, MirrorIgnoranceClient, MirrorConn>), "SendVoiceData",
            typeof(Il2CppSystem.ArraySegment<byte>));
        OpusEncoderEncode = Method(missing, typeof(OpusEncoder), "Encode",
            typeof(Il2CppSystem.ArraySegment<float>), typeof(Il2CppSystem.ArraySegment<byte>));
        EncoderPipelineEncodeFrames = Method(missing, typeof(EncoderPipeline), "EncodeFrames",
            typeof(IVoiceEncoder), typeof(int));
        MirrorClientSendReliable = Method(missing, typeof(MirrorIgnoranceClient), "SendReliable",
            typeof(Il2CppSystem.ArraySegment<byte>));
        MirrorClientSend = Method(missing, typeof(MirrorIgnoranceClient), "Send",
            typeof(Il2CppSystem.ArraySegment<byte>), typeof(byte));
        CommsNetworkPreprocessPacketToServer = Method(missing, typeof(MirrorIgnoranceCommsNetwork), "PreprocessPacketToServer",
            typeof(Il2CppSystem.ArraySegment<byte>));

        TriggerStart = Method(missing, typeof(VoiceBroadcastTrigger), "Start");
        TriggerUpdate = Method(missing, typeof(VoiceBroadcastTrigger), "Update");
        TriggerOpenChannel = Method(missing, typeof(VoiceBroadcastTrigger), "OpenChannel");
        TriggerCloseChannel = Method(missing, typeof(VoiceBroadcastTrigger), "CloseChannel");

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

    private static MethodInfo Method(List<string> missing, Type type, string name, params Type[] parameters)
    {
        MethodInfo method = null;
        try
        {
            method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, parameters, null);
        }
        catch (Exception)
        {
            // Ambiguous or otherwise unresolvable: treated as missing below.
        }
        if (method is null)
            missing.Add($"{type.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
        return method;
    }
}
