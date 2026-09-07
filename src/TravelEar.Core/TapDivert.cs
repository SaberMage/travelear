namespace TravelEar.Core;

/// <summary>
/// The Tap's divert step (CONTEXT.md: "The Tap always zeroes what it copies, so the game never
/// plays it"). Runs on Unity's audio thread inside the Tap postfix, so it must not allocate.
/// </summary>
public static class TapDivert
{
    // [impl->REQ-TAP-DIVERT]
    /// <summary>
    /// Copies <paramref name="data"/> into <paramref name="ring"/> and then zeroes
    /// <paramref name="data"/> in place, unconditionally: even when the ring is full and drops
    /// samples, the game's mixer still receives silence from this source. Returns the number of
    /// samples the ring accepted.
    /// </summary>
    public static int Divert(Span<float> data, VoiceRingBuffer ring)
    {
        var written = ring.Write(data);
        data.Clear();
        return written;
    }
}
