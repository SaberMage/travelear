using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class TransmitGateTests
{
    private const double Frame = 60; // ms, the game's Opus frame

    [Fact]
    public void Passes_the_first_frame_seen_while_transmitting()
    {
        var gate = new TransmitGate();
        Assert.Equal(GateDecision.Pass, gate.Decide(transmitting: true, nowMs: 0));
        Assert.Equal(1, gate.FramesPassed);
        Assert.Equal(0, gate.FramesSilenced);
    }

    [Fact]
    public void Silences_frames_when_nothing_has_ever_transmitted()
    {
        var gate = new TransmitGate();
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 0));
        Assert.Equal(GateDecision.Silence, gate.Decide(false, Frame));
        Assert.Equal(2, gate.FramesSilenced);
    }

    // [unit->REQ-VOICE-CONTINUOUS]
    [Fact]
    public void Holds_the_gate_open_for_the_release_hold_after_the_signal_drops()
    {
        var gate = new TransmitGate(releaseHoldMs: 100);
        Assert.Equal(GateDecision.Pass, gate.Decide(true, 0));
        Assert.Equal(GateDecision.Pass, gate.Decide(false, 60));     // within the hold: the word's tail
        Assert.Equal(GateDecision.Pass, gate.Decide(false, 100));    // hold boundary is inclusive
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 120)); // past the hold
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 180));
    }

    [Fact]
    public void Set_release_hold_applies_to_the_next_decision_and_keeps_the_counters()
    {
        var gate = new TransmitGate(releaseHoldMs: 100);
        gate.Decide(true, 0);
        gate.SetReleaseHold(500);
        Assert.Equal(500, gate.ReleaseHoldMs);
        Assert.Equal(GateDecision.Pass, gate.Decide(false, 400)); // inside the new hold
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 600));
        Assert.Equal(2, gate.FramesPassed);
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.SetReleaseHold(-1));
    }

    [Fact]
    public void A_new_onset_inside_the_hold_extends_it()
    {
        var gate = new TransmitGate(releaseHoldMs: 100);
        gate.Decide(true, 0);
        gate.Decide(false, 60);
        Assert.Equal(GateDecision.Pass, gate.Decide(true, 90));      // re-onset
        Assert.Equal(GateDecision.Pass, gate.Decide(false, 180));    // 90 ms after the re-onset
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 200));
    }

    [Fact]
    public void A_zero_hold_silences_the_first_frame_after_the_signal_drops()
    {
        var gate = new TransmitGate(releaseHoldMs: 0);
        Assert.Equal(GateDecision.Pass, gate.Decide(true, 0));
        Assert.Equal(GateDecision.Silence, gate.Decide(false, Frame));
    }

    [Fact]
    public void Reopens_immediately_on_the_next_onset()
    {
        var gate = new TransmitGate();
        gate.Decide(true, 0);
        Assert.Equal(GateDecision.Silence, gate.Decide(false, 1000));
        Assert.Equal(GateDecision.Pass, gate.Decide(true, 1060));
    }

    // [unit->REQ-VOICE-CONTINUOUS]
    [Fact]
    public void Never_drops_a_frame_whatever_the_signal_does()
    {
        var gate = new TransmitGate();
        var random = new Random(20260907);
        const int frames = 5000;
        for (var i = 0; i < frames; i++)
        {
            var decision = gate.Decide(random.Next(4) == 0, i * Frame);
            Assert.True(decision is GateDecision.Pass or GateDecision.Silence);
        }
        // One push per frame either way: the ring's write head advances exactly `frames` times.
        Assert.Equal(frames, gate.FramesPassed + gate.FramesSilenced);
        Assert.True(gate.FramesPassed > 0);
        Assert.True(gate.FramesSilenced > 0);
    }

    [Fact]
    public void Rejects_a_negative_hold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransmitGate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransmitGate(double.NaN));
    }
}
