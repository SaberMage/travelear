using System.Reflection;
using HarmonyLib;

namespace TravelEar;

/// <summary>
/// Spike S2c: tap the Opus encoder itself. <c>OpusEncoder.Encode(samples, buffer)</c> is
/// non-generic (so IL2CPP generic sharing cannot route calls around the detour) and returns the
/// exact encoded bytes Dissonance goes on to send. By CONTEXT.md this output <em>is</em>
/// Outbound Voice: mic audio after the game's capture preprocessing and Opus encoding.
/// </summary>
[HarmonyPatch]
internal static class OpusEncodeTap
{
    /// <summary>Raised for every encoded frame with a private copy of the Opus bytes and a local sequence number.</summary>
    public static event Action<int, byte[]> FrameEncoded;

    public static long Frames;
    private const int Verbose = 5;
    private const int SummaryEvery = 100;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.OpusEncoderEncode;

    // [impl->REQ-VOICE-OUTBOUND-TAP]
    [HarmonyPostfix]
    private static void Postfix(Il2CppSystem.ArraySegment<float> samples, Il2CppSystem.ArraySegment<byte> __result)
    {
        try
        {
            var array = __result.Array;
            var count = __result.Count;
            var offset = __result.Offset;
            var bytes = count > 0 && array is not null ? new byte[count] : Array.Empty<byte>();
            for (var i = 0; i < bytes.Length; i++) bytes[i] = array[offset + i];

            var n = (int)Interlocked.Increment(ref Frames);
            if (n <= Verbose)
                Plugin.Logger.LogInfo($"OpusTap: frame #{n} in={samples.Count} samples out={bytes.Length} bytes toc=0x{(bytes.Length > 0 ? bytes[0] : 0):X2}");
            else if (n % SummaryEvery == 0)
                Plugin.Logger.LogInfo($"OpusTap: {n} frames, last out={bytes.Length} bytes");

            FrameEncoded?.Invoke(n, bytes);
        }
        catch (Exception e)
        {
            if (Interlocked.Read(ref Frames) <= Verbose)
                Plugin.Logger.LogError($"OpusTap: postfix threw: {e}");
        }
    }
}

/// <summary>Spike canary next to <see cref="OpusEncodeTap"/>: how many frames the pipeline encodes per call.</summary>
[HarmonyPatch]
internal static class EncodeFramesCanary
{
    private static long _calls;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.EncoderPipelineEncodeFrames;

    [HarmonyPostfix]
    private static void Postfix(int maxCount, int __result)
    {
        var n = Interlocked.Increment(ref _calls);
        if (n <= 5 || n % 500 == 0)
            Plugin.Logger.LogInfo($"Canary: EncodeFrames #{n} max={maxCount} encoded={__result}");
    }
}
