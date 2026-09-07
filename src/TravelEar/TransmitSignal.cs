using System.Reflection;
using System.Text;
using Dissonance;
using HarmonyLib;

namespace TravelEar;

/// <summary>
/// The "peers receive" signal for the transmit gate (M2 T0, M2-PLAN question 1, settled by
/// runs 1-2), read on the main thread each tick. Peers receive while the comms are not muted and
/// either a room other than the game's always-open <c>Echo</c> room is open (the token rooms:
/// radio, megaphone, GhostRoom once its channel opens) or any voice-activation
/// <c>VoiceBroadcastTrigger</c> reports its VAD speaking.
/// <para>
/// Why the VAD flag and not the open channels alone: the game's "Self Echo" trigger holds the
/// Echo room open in Open mode, so the encoder runs continuously while unmuted and says nothing
/// about peers; the peer-facing channels are voice-activation triggers (GhostRoom and the
/// proximity trigger) whose channel opens on VAD, and in a solo session that channel never opens
/// (no peer, no collider) while the VAD flag still flips with speech. The open-room test stays
/// as the complement for the Open-mode token rooms, which transmit without VAD.
/// </para>
/// <para>
/// Triggers are collected by a postfix on their <c>Start</c>, which runs for every trigger in
/// the scene (<c>Object.FindObjectOfType</c> is stripped from this build). Any failure to read
/// the signal reports <see cref="Sample.Available"/> false and the caller fails open (renders
/// everything), never closed: a broken read must not silence Local Voice (docs/KNOWN-HAZARDS.md,
/// no partial fidelity means no silent degradation either way).
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

    public readonly struct Sample
    {
        public Sample(bool available, bool peersReceive, bool muted, bool vadSpeaking, string rooms, string triggersTransmitting)
        {
            Available = available;
            PeersReceive = peersReceive;
            Muted = muted;
            VadSpeaking = vadSpeaking;
            RoomsText = rooms;
            TriggersText = triggersTransmitting;
        }

        /// <summary>False until the world and its DissonanceComms exist, or when the read failed.</summary>
        public bool Available { get; }

        /// <summary>Not muted, and a token room is open or a voice-activation trigger hears speech.</summary>
        public bool PeersReceive { get; }

        public bool Muted { get; }
        public bool VadSpeaking { get; }
        public string RoomsText { get; }
        public string TriggersText { get; }

        /// <summary>One line for the state-change log; equal strings mean an unchanged state.</summary>
        public string Describe() =>
            Available
                ? $"muted {Muted}, vad {VadSpeaking}, rooms=[{RoomsText}], transmitting=[{TriggersText}] -> gate {(PeersReceive ? "OPEN" : "closed")}"
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

    /// <summary>
    /// Main thread. The channel fade of the game's voice-activation triggers, from their own
    /// <c>VolumeFaderSettings</c>: the longest fade-out among them and that trigger's fade-in.
    /// False until a voice-activation trigger has started. Throws on an interop failure (the
    /// caller logs once and keeps the hard gate).
    /// </summary>
    public static bool TryReadFade(out double fadeInMs, out double fadeOutMs, out string source)
    {
        fadeInMs = 0;
        fadeOutMs = -1;
        source = null;
        foreach (var trigger in Triggers)
        {
            if (trigger is null || trigger.Pointer == IntPtr.Zero) continue;
            if (trigger.Mode != CommActivationMode.VoiceActivation) continue;
            var settings = trigger._activationFaderSettings;
            if (settings is null) continue;
            var outMs = settings._fadeOutTicks / 10000.0; // TimeSpan ticks
            if (outMs <= fadeOutMs) continue;
            fadeOutMs = outMs;
            fadeInMs = settings._fadeInTicks / 10000.0;
            var name = trigger.RoomName;
            source = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }
        return fadeOutMs >= 0;
    }

    /// <summary>Main thread. Never throws; a failed read returns an unavailable sample and the error.</summary>
    public static Sample Read(out Exception error)
    {
        error = null;
        try
        {
            var comms = WorldManager.instance?.dissonanceComms;
            if (comms is null) return default;

            var muted = comms.IsMuted;

            Rooms.Clear();
            var tokenRoomOpen = false;
            var roomChannels = comms.RoomChannels?._openChannelsBySubId;
            if (roomChannels is not null)
            {
                var rooms = roomChannels.Values.GetEnumerator();
                while (rooms.MoveNext())
                {
                    var name = rooms.Current._roomId.Name;
                    if (Rooms.Length > 0) Rooms.Append(", ");
                    Rooms.Append(name);
                    if (!string.Equals(name, EchoRoom, StringComparison.Ordinal)) tokenRoomOpen = true;
                }
            }

            Transmitting.Clear();
            var vadSpeaking = false;
            for (var i = Triggers.Count - 1; i >= 0; i--)
            {
                var trigger = Triggers[i];
                if (trigger is null || trigger.Pointer == IntPtr.Zero)
                {
                    Triggers.RemoveAt(i);
                    continue;
                }
                if (trigger.Mode == CommActivationMode.VoiceActivation && trigger._isVadSpeaking) vadSpeaking = true;
                if (!trigger.IsTransmitting) continue;
                if (Transmitting.Length > 0) Transmitting.Append(", ");
                Transmitting.Append(trigger.RoomName).Append('(').Append(trigger.Mode).Append(')');
            }

            var peersReceive = !muted && (tokenRoomOpen || vadSpeaking);
            return new Sample(true, peersReceive, muted, vadSpeaking, Rooms.ToString(), Transmitting.ToString());
        }
        catch (Exception e)
        {
            error = e;
            return default;
        }
    }
}
