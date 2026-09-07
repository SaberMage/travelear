using System.Reflection;
using HarmonyLib;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Spike S2b: tap the encoded Opus frame one step before packetisation.
/// <c>BaseClient.SendVoiceData(ArraySegment&lt;byte&gt; encodedAudio)</c> is called for every frame
/// while the local player is transmitting, regardless of whether any peer is listening; the
/// Mirror send (<see cref="OutboundVoiceTap"/>) only fires when Dissonance has someone to send
/// to. If this fires in a solo session, T3 decodes from here.
/// </summary>
[HarmonyPatch]
internal static class EncodedVoiceTap
{
    /// <summary>Raised for every encoded frame with a private copy of the Opus bytes and a local sequence number.</summary>
    public static event Action<int, byte[]> FrameEncoded;

    public static long Frames;
    private const int Verbose = 5;
    private const int SummaryEvery = 100;

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.BaseClientSendVoiceData;

    // [impl->REQ-VOICE-OUTBOUND-TAP]
    [HarmonyPostfix]
    private static void Postfix(Il2CppSystem.ArraySegment<byte> encodedAudio)
    {
        try
        {
            var array = encodedAudio.Array;
            var count = encodedAudio.Count;
            var offset = encodedAudio.Offset;
            var bytes = count > 0 && array is not null ? new byte[count] : Array.Empty<byte>();
            for (var i = 0; i < bytes.Length; i++) bytes[i] = array[offset + i];

            var n = (int)Interlocked.Increment(ref Frames);
            if (n <= Verbose)
                Plugin.Logger.LogInfo($"EncodedTap: frame #{n} {bytes.Length} bytes opusTOC=0x{(bytes.Length > 0 ? bytes[0] : 0):X2}");
            else if (n % SummaryEvery == 0)
                Plugin.Logger.LogInfo($"EncodedTap: {n} frames, last {bytes.Length} bytes");

            FrameEncoded?.Invoke(n, bytes);
        }
        catch (Exception e)
        {
            if (Interlocked.Read(ref Frames) <= Verbose)
                Plugin.Logger.LogError($"EncodedTap: postfix threw: {e}");
        }
    }
}
