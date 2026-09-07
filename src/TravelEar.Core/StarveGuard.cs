namespace TravelEar.Core;

/// <summary>
/// The Helper's re-prime rule (ADR-0005): with the Sink fed from the encoder thread the stream
/// stops whenever the game stops encoding (mute, menu), so the playback ring drains. Playing the
/// next burst from an empty ring would spend every arrival jitter as an underrun click, so once
/// the ring has starved the reader emits silence until the backlog is back at the jitter target,
/// then resumes. At stream start the caller primes the ring instead, so this only acts after a
/// starve. Pure policy, no audio; the reader applies it.
/// </summary>
public sealed class StarveGuard
{
    private readonly int _targetBacklogSamples;

    /// <summary>Number of times the ring ran dry and playback was held for a re-prime.</summary>
    public long Starves { get; private set; }

    /// <summary>True while playback is held, waiting for the backlog to reach the target.</summary>
    public bool Holding { get; private set; }

    public StarveGuard(int targetBacklogSamples)
    {
        _targetBacklogSamples = Math.Max(0, targetBacklogSamples);
    }

    // [impl->REQ-SINK-FORMAT]
    /// <summary>
    /// Decides one read of <paramref name="requested"/> samples against a ring holding
    /// <paramref name="backlog"/>: true = output silence and leave the ring alone, false = read.
    /// A read that would run the ring dry starts a hold; the hold ends when the backlog reaches
    /// the target (or the requested size, whichever is larger).
    /// </summary>
    public bool ShouldHold(int backlog, int requested)
    {
        var resumeAt = Math.Max(_targetBacklogSamples, requested);
        if (Holding)
        {
            if (backlog < resumeAt) return true;
            Holding = false;
            return false;
        }
        if (backlog < requested)
        {
            Holding = true;
            Starves++;
            return true;
        }
        return false;
    }
}
