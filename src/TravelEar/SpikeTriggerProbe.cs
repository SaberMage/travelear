using System.Reflection;
using Dissonance;
using HarmonyLib;

namespace TravelEar;

/// <summary>
/// M1 spike S2 diagnostics only (removed at T4). Reports the state of every Dissonance
/// VoiceBroadcastTrigger the game creates, to learn why a solo host never transmits:
/// activation mode, channel type, room, mute, VAD, user activation, open/close events.
/// </summary>
internal static class SpikeTriggerProbe
{
    private const int EveryFrames = 300; // ~5 s at 60 fps, per trigger

    private static readonly Dictionary<int, int> FrameCounts = new();

    [HarmonyPatch]
    private static class StartPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.TriggerStart;

        [HarmonyPostfix]
        private static void Postfix(VoiceBroadcastTrigger __instance) => Report("Start", __instance);
    }

    [HarmonyPatch]
    private static class UpdatePatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.TriggerUpdate;

        [HarmonyPostfix]
        private static void Postfix(VoiceBroadcastTrigger __instance)
        {
            var id = __instance.GetInstanceID();
            FrameCounts.TryGetValue(id, out var n);
            n++;
            FrameCounts[id] = n;
            if (n % EveryFrames == 1) Report($"Update#{n}", __instance);
        }
    }

    [HarmonyPatch]
    private static class OpenPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.TriggerOpenChannel;

        [HarmonyPostfix]
        private static void Postfix(VoiceBroadcastTrigger __instance) => Report("OpenChannel", __instance);
    }

    [HarmonyPatch]
    private static class ClosePatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => GameSymbols.TriggerCloseChannel;

        [HarmonyPostfix]
        private static void Postfix(VoiceBroadcastTrigger __instance) => Report("CloseChannel", __instance);
    }

    private static void Report(string evt, VoiceBroadcastTrigger t)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"TriggerProbe[{t.GetInstanceID()}] {evt}: ");
            sb.Append(Safe(() => $"go={t.gameObject.name} enabled={t.enabled}"));
            sb.Append(Safe(() => $" mode={t.Mode} type={t.ChannelType} room='{t.RoomName}' player='{t.PlayerId}' input='{t.InputName}'"));
            sb.Append(Safe(() => $" muted={t.IsMuted} transmitting={t.IsTransmitting} canTrigger={t.CanTrigger} userActivated={t.IsUserActivated()} vad={t._isVadSpeaking} fader={t.CurrentFaderVolume:F2}"));
            sb.Append(Safe(() => $" tokenAct={t.TokenActivationState} colliderTrig={t.IsColliderTriggered} useCollider={t.UseColliderTrigger}"));

            var comms = t.Comms;
            if (comms is not null)
            {
                sb.Append(Safe(() => $" | comms: started={comms.IsStarted} net={comms.IsNetworkInitialized} muted={comms.IsMuted} deafened={comms.IsDeafened} local='{comms.LocalPlayerName}'"));
                sb.Append(Safe(() => $" rooms={comms.RoomChannels.Count} playerChans={comms.PlayerChannels.Count} players={comms.Players.Count}"));
                sb.Append(Safe(() =>
                {
                    var players = comms.Players;
                    for (var i = 0; i < players.Count; i++)
                    {
                        var p = players[i];
                        if (p.IsLocalPlayer) return $" localState: speaking={p.IsSpeaking} amp={p.Amplitude:F3} connected={p.IsConnected}";
                    }
                    return " localState: none";
                }));
            }
            else sb.Append(" | comms: null");

            Plugin.Logger.LogInfo(sb.ToString());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"TriggerProbe report failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return $" [{e.GetType().Name}]"; }
    }

    public static void PatchAll(Harmony harmony)
    {
        harmony.PatchAll(typeof(StartPatch));
        harmony.PatchAll(typeof(UpdatePatch));
        harmony.PatchAll(typeof(OpenPatch));
        harmony.PatchAll(typeof(ClosePatch));
    }
}
