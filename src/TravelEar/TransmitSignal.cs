using System.Reflection;
using System.Text;
using Dissonance;
using HarmonyLib;

namespace TravelEar;

/// <summary>
/// The "peers receive" signal for the transmit gate (M2 T0, M2-PLAN open question 1), read on
/// the main thread each tick. The signal used is (a): any room channel open in
/// <c>WorldManager.instance.dissonanceComms.RoomChannels</c> whose room is not the game's
/// always-open <c>Echo</c> room (the "Self Echo" trigger keeps it open all session; nothing is
/// sent to peers through it). It covers voice activation (the GhostRoom trigger), push-to-talk
/// and, later, the radio and megaphone rooms without naming any of them.
/// <para>
/// One-run probe (remove after the T0 run): the two alternatives, (b) every
/// <c>VoiceBroadcastTrigger</c> whose <c>IsTransmitting</c> is set and (c) the open player
/// channels, are read alongside so the renderer can log all three once per state change and the
/// run can confirm (a) tracks speech onsets. Triggers are collected by a postfix on their
/// <c>Start</c>, which runs for every trigger in the scene (the game has no other way to find
/// them: <c>Object.FindObjectOfType</c> is stripped from this build).
/// </para>
/// <para>
/// Any failure to read the signal reports <see cref="Sample.Available"/> false and the caller
/// fails open (renders everything), never closed: a broken probe must not silence Local Voice
/// (docs/KNOWN-HAZARDS.md, no partial fidelity means no silent degradation either way).
/// </para>
/// </summary>
[HarmonyPatch]
internal static class TransmitSignal
{
    /// <summary>The game's always-open self-monitoring room; frames sent only there reach no peer.</summary>
    public const string EchoRoom = "Echo";

    private static readonly List<VoiceBroadcastTrigger> Triggers = new(); // main thread only
    private static readonly StringBuilder Rooms = new();
    private static readonly StringBuilder Transmitting = new();
    private static readonly StringBuilder Players = new();
    private static readonly StringBuilder Local = new();

    public readonly struct Sample
    {
        public Sample(bool available, bool peersReceive, string rooms, string triggers, string playerChannels, string local)
        {
            Available = available;
            PeersReceive = peersReceive;
            RoomsText = rooms;
            TriggersText = triggers;
            PlayersText = playerChannels;
            LocalText = local;
        }

        /// <summary>False until the world and its DissonanceComms exist, or when the read failed.</summary>
        public bool Available { get; }

        /// <summary>Signal (a): a room other than <see cref="EchoRoom"/> is open.</summary>
        public bool PeersReceive { get; }

        public string RoomsText { get; }
        public string TriggersText { get; }
        public string PlayersText { get; }

        /// <summary>Probe: the comms mute flags and the local player's own speaking state.</summary>
        public string LocalText { get; }

        /// <summary>One line for the state-change log; equal strings mean an unchanged state.</summary>
        public string Describe() =>
            Available
                ? $"rooms=[{RoomsText}] triggers=[{TriggersText}] playerChannels=[{PlayersText}] local=[{LocalText}] -> gate {(PeersReceive ? "OPEN" : "closed")}"
                : "unavailable (no DissonanceComms yet) -> gate OPEN (fail open)";
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.VoiceBroadcastTriggerStart;

    [HarmonyPostfix]
    private static void TrackTrigger(VoiceBroadcastTrigger __instance)
    {
        try
        {
            var pointer = __instance.Pointer;
            foreach (var known in Triggers)
                if (known.Pointer == pointer) return;
            Triggers.Add(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Transmit signal: could not track a VoiceBroadcastTrigger: {e.Message}");
        }
    }

    /// <summary>Main thread. Never throws; a failed read returns an unavailable sample and the error.</summary>
    public static Sample Read(out Exception error)
    {
        error = null;
        try
        {
            var comms = WorldManager.instance?.dissonanceComms;
            if (comms is null) return default;

            Rooms.Clear();
            var peersReceive = false;
            var roomChannels = comms.RoomChannels?._openChannelsBySubId;
            if (roomChannels is not null)
            {
                var rooms = roomChannels.Values.GetEnumerator();
                while (rooms.MoveNext())
                {
                    var name = rooms.Current._roomId.Name;
                    if (Rooms.Length > 0) Rooms.Append(", ");
                    Rooms.Append(name);
                    if (!string.Equals(name, EchoRoom, StringComparison.Ordinal)) peersReceive = true;
                }
            }

            Players.Clear();
            var playerChannels = comms.PlayerChannels?._openChannelsBySubId;
            if (playerChannels is not null)
            {
                var players = playerChannels.Values.GetEnumerator();
                while (players.MoveNext())
                {
                    if (Players.Length > 0) Players.Append(", ");
                    Players.Append(players.Current._playerId);
                }
            }

            // Probe: every tracked trigger, as Room(Mode) plus flags: '*' transmitting, 'M' muted,
            // 'V' the trigger's own VAD says speaking. A trigger that never shows '*' while 'V' is
            // set is being held shut by something other than voice activity.
            Transmitting.Clear();
            for (var i = Triggers.Count - 1; i >= 0; i--)
            {
                var trigger = Triggers[i];
                if (trigger is null || trigger.Pointer == IntPtr.Zero)
                {
                    Triggers.RemoveAt(i);
                    continue;
                }
                if (Transmitting.Length > 0) Transmitting.Append(", ");
                Transmitting.Append(trigger.RoomName).Append('(').Append(trigger.Mode).Append(')');
                if (trigger.IsTransmitting) Transmitting.Append('*');
                if (trigger.IsMuted) Transmitting.Append('M');
                if (trigger._isVadSpeaking) Transmitting.Append('V');
            }

            Local.Clear();
            Local.Append("commsMuted ").Append(comms.IsMuted);
            var localName = comms.LocalPlayerName;
            var localState = localName is null ? null : comms.FindPlayer(localName);
            Local.Append(", speaking ").Append(localState is null ? "n/a" : localState.IsSpeaking.ToString());

            return new Sample(true, peersReceive, Rooms.ToString(), Transmitting.ToString(), Players.ToString(), Local.ToString());
        }
        catch (Exception e)
        {
            error = e;
            return default;
        }
    }
}
