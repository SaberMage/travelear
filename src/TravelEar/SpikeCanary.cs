using System.Reflection;
using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// M1 spike S2 diagnostics only (removed at T4). Answers, independently of voice traffic, whether
/// Harmony postfixes on the Mirror client fire under IL2CPP: the Dissonance handshake and
/// client-state packets always pass through <c>SendReliable</c> at session join, every send
/// passes through <c>Send(packet, channel)</c>, and on a host every client→server packet loops
/// back through <c>MirrorIgnoranceCommsNetwork.PreprocessPacketToServer</c>.
/// </summary>
internal static class SpikeCanary
{
    private const int Verbose = 5;
    private static long _reliable, _send, _loopback;

    [HarmonyPatch]
    private static class SendReliablePatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.MirrorClientSendReliable;

        [HarmonyPostfix]
        private static void Postfix(Il2CppSystem.ArraySegment<byte> packet)
        {
            var n = Interlocked.Increment(ref _reliable);
            if (n <= Verbose) Plugin.Logger.LogInfo($"Canary: SendReliable #{n} {Describe(packet)}");
        }
    }

    [HarmonyPatch]
    private static class SendPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.MirrorClientSend;

        [HarmonyPostfix]
        private static void Postfix(Il2CppSystem.ArraySegment<byte> packet, byte channel, bool __result)
        {
            var n = Interlocked.Increment(ref _send);
            if (n <= Verbose || n % 250 == 0)
                Plugin.Logger.LogInfo($"Canary: Send #{n} channel={channel} ok={__result} {Describe(packet)}");
        }
    }

    [HarmonyPatch]
    private static class LoopbackPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.CommsNetworkPreprocessPacketToServer;

        [HarmonyPostfix]
        private static void Postfix(Il2CppSystem.ArraySegment<byte> packet, bool __result)
        {
            var n = Interlocked.Increment(ref _loopback);
            if (n <= Verbose || n % 250 == 0)
                Plugin.Logger.LogInfo($"Canary: PreprocessPacketToServer #{n} handled={__result} {Describe(packet)}");
        }
    }

    private static string Describe(Il2CppSystem.ArraySegment<byte> packet)
    {
        try
        {
            var array = packet.Array;
            var count = packet.Count;
            var offset = packet.Offset;
            if (array is null || count <= 0) return "(empty)";
            var head = new byte[Math.Min(count, 8)];
            for (var i = 0; i < head.Length; i++) head[i] = array[offset + i];
            var type = DissonanceFrame.TryGetMessageType(head, out var t) ? t.ToString() : "no-magic";
            return $"{count} bytes type={type} head={BitConverter.ToString(head)}";
        }
        catch (Exception e)
        {
            return $"(describe failed: {e.GetType().Name})";
        }
    }

    public static void PatchAll(Harmony harmony)
    {
        harmony.PatchAll(typeof(SendReliablePatch));
        harmony.PatchAll(typeof(SendPatch));
        harmony.PatchAll(typeof(LoopbackPatch));
    }
}
