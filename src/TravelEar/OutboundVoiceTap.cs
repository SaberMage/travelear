using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// The Outbound Voice tap: a Harmony postfix on the Dissonance Mirror client's unreliable send.
/// Copies each packet the game sends to peers, parses it as a Dissonance VoiceData frame, and
/// hands the frame to <see cref="FrameTapped"/>. Push-to-talk, voice activation, and FEC state
/// are reproduced by construction because these are the real packets.
/// </summary>
[HarmonyPatch]
internal static class OutboundVoiceTap
{
    /// <summary>Raised on the game's network thread for every well-formed VoiceData frame. The frame's payload is a private copy.</summary>
    public static event Action<DissonanceFrame> FrameTapped;

    public static long PacketsSeen;
    public static long VoiceFrames;
    public static long Rejected;

    private const int VerboseFrames = 5;       // log the first few parsed frames in full at Info
    private const int SummaryEvery = 250;      // then one Info line per this many packets

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.MirrorClientSendUnreliable;

    // [impl->REQ-VOICE-OUTBOUND-TAP]
    [HarmonyPostfix]
    private static void Postfix(Il2CppSystem.ArraySegment<byte> packet)
    {
        try
        {
            var bytes = Copy(packet);
            var seen = Interlocked.Increment(ref PacketsSeen);
            if (seen <= VerboseFrames)
            {
                var type = DissonanceFrame.TryGetMessageType(bytes, out var t) ? t.ToString() : "no-magic";
                Plugin.Logger.LogInfo($"Tap: SendUnreliable #{seen} {bytes.Length} bytes type={type} head={Hex(bytes, 8)}");
            }

            if (!DissonanceFrame.TryParse(bytes, out var frame, out var error))
            {
                if (error != DissonanceFrameError.NotVoiceData)
                {
                    var rejected = Interlocked.Increment(ref Rejected);
                    if (rejected <= VerboseFrames)
                        Plugin.Logger.LogWarning($"Tap: packet #{seen} rejected ({error}), {bytes.Length} bytes: {Hex(bytes, 24)}");
                }
                return;
            }

            var count = Interlocked.Increment(ref VoiceFrames);
            if (count <= VerboseFrames)
            {
                var channels = string.Join(",", frame.Channels.Select(c => $"{c.Bitfield:X4}/{c.Id:X4}"));
                Plugin.Logger.LogInfo($"Tap: VoiceData seq={frame.Sequence} session={frame.SessionId:X8} sender={frame.SenderId} " +
                                      $"chanSession={frame.ChannelSession} channels=[{channels}] payload={frame.Payload.Length} bytes");
            }
            else if (seen % SummaryEvery == 0)
            {
                Plugin.Logger.LogInfo($"Tap: {seen} packets, {count} VoiceData frames, {Volatile.Read(ref Rejected)} rejected; last seq={frame.Sequence} payload={frame.Payload.Length}");
            }

            FrameTapped?.Invoke(frame);
        }
        catch (Exception e)
        {
            // Never let the tap disturb the game's send path.
            if (Interlocked.Increment(ref Rejected) <= VerboseFrames)
                Plugin.Logger.LogError($"Tap: postfix threw: {e}");
        }
    }

    /// <summary>Copies the segment out of IL2CPP memory into a managed array the parser can slice.</summary>
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

    private static string Hex(byte[] bytes, int max)
    {
        var n = Math.Min(max, bytes.Length);
        var s = BitConverter.ToString(bytes, 0, n);
        return n < bytes.Length ? s + "..." : s;
    }
}
