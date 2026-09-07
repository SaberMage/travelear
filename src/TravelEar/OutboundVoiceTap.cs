using System.Reflection;
using HarmonyLib;

namespace TravelEar;

/// <summary>
/// The Outbound Voice tap: a Harmony postfix on Dissonance's <c>OpusEncoder.Encode</c>. Its
/// return value is the exact encoded frame the game goes on to send to peers, produced from the
/// mic audio after the game's capture preprocessing, so by CONTEXT.md it <em>is</em> Outbound
/// Voice. It fires whenever the local player transmits, whether or not any peer is listening.
/// <para>
/// Why not the network send (M1 spike S2, runs 1-5): a host with no listeners never builds a
/// VoiceData packet, and a postfix on the generic <c>BaseClient&lt;...&gt;.SendVoiceData</c>
/// installs but never fires under IL2CPP generic sharing. <c>OpusEncoder.Encode</c> is
/// non-generic and upstream of both. The VoiceData wire parser (<c>TravelEar.Core.DissonanceFrame</c>)
/// stays as tested reference code.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class OutboundVoiceTap
{
    /// <summary>
    /// Raised on Dissonance's encoder thread for every encoded frame with a local sequence
    /// number and a private copy of the Opus bytes. Handlers must be cheap and must not touch
    /// Unity objects.
    /// </summary>
    public static event Action<int, byte[]> FrameEncoded;

    public static long Frames;
    public static int LastInputSamples;

    private const int VerboseFrames = 3;
    private const int SummaryEvery = 500;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.OpusEncoderEncode;

    // [impl->REQ-VOICE-OUTBOUND-TAP]
    [HarmonyPostfix]
    private static void Postfix(Il2CppSystem.ArraySegment<float> samples, Il2CppSystem.ArraySegment<byte> __result)
    {
        try
        {
            var bytes = Copy(__result);
            LastInputSamples = samples.Count;
            var n = (int)Interlocked.Increment(ref Frames);

            if (n <= VerboseFrames)
                Plugin.Logger.LogInfo($"Tap: frame #{n} in={samples.Count} samples out={bytes.Length} bytes toc=0x{(bytes.Length > 0 ? bytes[0] : 0):X2}");
            else if (n % SummaryEvery == 0)
                Plugin.Logger.LogInfo($"Tap: {n} frames encoded; last {bytes.Length} bytes");

            FrameEncoded?.Invoke(n, bytes);
        }
        catch (Exception e)
        {
            // Never let the tap disturb the game's encode path.
            if (Interlocked.Read(ref Frames) <= VerboseFrames)
                Plugin.Logger.LogError($"Tap: postfix threw: {e}");
        }
    }

    /// <summary>Copies the segment out of IL2CPP memory into a managed array.</summary>
    private static byte[] Copy(Il2CppSystem.ArraySegment<byte> segment)
    {
        var array = segment.Array;
        var count = segment.Count;
        var offset = segment.Offset;
        if (array is null || count <= 0) return Array.Empty<byte>();
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = array[offset + i];
        return bytes;
    }
}
