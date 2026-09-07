namespace TravelEar.Core;

/// <summary>What the gate wants done with one decoded Outbound Voice frame.</summary>
public enum GateDecision
{
    /// <summary>Push the decoded frame: peers are receiving this audio.</summary>
    Pass,

    /// <summary>Push a frame of zeros of the same length instead: peers are not receiving it.</summary>
    Silence,
}

/// <summary>
/// The transmit gate (M2 T0, docs/DESIGN.md "Continuity"): decides, per decoded frame, whether the
/// Local Voice ring receives the frame or an equal length of silence. The encoder runs whenever the
/// mic is open (push-to-talk defaults to toggle-on and the game's "Self Echo" room channel is
/// always open), so Outbound Voice carries the mic noise floor between words; peers, however, only
/// receive frames while a voice-activation or push-to-talk channel is open. Gating on that signal
/// removes the noise floor without ever removing a frame: the ring stays fresh and continuous.
/// <para>
/// A short release hold keeps the gate open after the signal drops so the tail of a word is not
/// cut when the game's channel closes a frame or two early. There is no attack delay: the first
/// frame seen while transmitting passes.
/// </para>
/// <para>
/// Pure and single-threaded: the caller (the encoder thread) samples the transmit signal and the
/// clock and passes both in. Counters may be read from any thread for diagnostics.
/// </para>
/// </summary>
public sealed class TransmitGate
{
    /// <summary>Default release hold: about two 60 ms Opus frames.</summary>
    public const double DefaultReleaseHoldMs = 100;

    private double _releaseHoldMs;
    private double _lastTransmitMs = double.NegativeInfinity;
    private long _framesPassed;
    private long _framesSilenced;

    public TransmitGate(double releaseHoldMs = DefaultReleaseHoldMs)
    {
        SetReleaseHold(releaseHoldMs);
    }

    /// <summary>How long the gate stays open after the transmit signal drops, in milliseconds.</summary>
    public double ReleaseHoldMs => _releaseHoldMs;

    /// <summary>
    /// Changes the hold without touching the counters or the last-transmit time; the next
    /// decision uses it. The renderer grows the hold to cover the channel fade-out once the
    /// game's fade times are known (<see cref="TransmitFader"/>).
    /// </summary>
    public void SetReleaseHold(double releaseHoldMs)
    {
        if (releaseHoldMs < 0 || double.IsNaN(releaseHoldMs))
            throw new ArgumentOutOfRangeException(nameof(releaseHoldMs), releaseHoldMs, "The release hold must be zero or positive.");
        _releaseHoldMs = releaseHoldMs;
    }

    /// <summary>Frames that passed through since construction.</summary>
    public long FramesPassed => Volatile.Read(ref _framesPassed);

    /// <summary>Frames replaced by silence since construction.</summary>
    public long FramesSilenced => Volatile.Read(ref _framesSilenced);

    // [impl->REQ-VOICE-CONTINUOUS]
    /// <summary>
    /// Decides for one frame. <paramref name="transmitting"/> is the "peers receive" signal as
    /// sampled for this frame; <paramref name="nowMs"/> is a monotonic clock in milliseconds. Every
    /// call yields exactly one of <see cref="GateDecision.Pass"/> or <see cref="GateDecision.Silence"/>:
    /// the gate never drops a frame, so the ring's write head advances by one frame per call
    /// whichever way it decides (the never-gap invariant).
    /// </summary>
    public GateDecision Decide(bool transmitting, double nowMs)
    {
        if (transmitting) _lastTransmitMs = nowMs;
        var open = transmitting || nowMs - _lastTransmitMs <= _releaseHoldMs;
        if (open) Interlocked.Increment(ref _framesPassed);
        else Interlocked.Increment(ref _framesSilenced);
        return open ? GateDecision.Pass : GateDecision.Silence;
    }
}
