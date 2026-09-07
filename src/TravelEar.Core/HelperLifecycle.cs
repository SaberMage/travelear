namespace TravelEar.Core;

/// <summary>Why the launcher did or did not spawn the Helper this time.</summary>
public enum SpawnDecision
{
    /// <summary>Spawn it now. Returned at most once per <see cref="HelperLifecycle"/>.</summary>
    Spawn,

    /// <summary>Config <c>SpawnHelper</c> is off: the operator starts the Helper by hand.</summary>
    SkipDisabled,

    /// <summary>The Helper executable is not where <c>HelperPath</c> (or the default) points.</summary>
    SkipMissing,

    /// <summary>A Helper process already exists (started by hand or by an earlier game launch).</summary>
    SkipAlreadyRunning,

    /// <summary>This lifecycle already spawned once; it never respawns, whatever became of that process.</summary>
    SkipAlreadySpawned,
}

/// <summary>
/// The Sink lifecycle policy (docs/DESIGN.md "Sink transport and Helper", <c>REQ-SINK-LIFECYCLE</c>):
/// the mod spawns the Helper at most once per game launch and never respawns it, and the pipe
/// server is re-armed no more often than every <see cref="RetryIntervalMs"/> after a disconnect.
/// Pure: the launcher and the pump feed it what they observe and act on what it decides, so the
/// "never respawn" and "retry every 5 s" promises are unit-tested without a process or a pipe.
/// A failed spawn is absorbed the same way as a successful one: recorded, never retried, and never
/// thrown (docs/KNOWN-HAZARDS.md 2.1).
/// </summary>
public sealed class HelperLifecycle
{
    /// <summary>Minimum spacing between two pipe re-arms after a disconnect.</summary>
    public const double RetryIntervalMs = 5000;

    private readonly double _retryIntervalMs;
    private bool _spawnDecided;
    private bool _spawnFailed;
    private double _lastArmMs = double.NegativeInfinity;
    private int _armAttempts;

    public HelperLifecycle(double retryIntervalMs = RetryIntervalMs)
    {
        if (retryIntervalMs < 0 || double.IsNaN(retryIntervalMs))
            throw new ArgumentOutOfRangeException(nameof(retryIntervalMs), retryIntervalMs, "The retry interval must be zero or positive.");
        _retryIntervalMs = retryIntervalMs;
    }

    /// <summary>True once <see cref="DecideSpawn"/> has returned <see cref="SpawnDecision.Spawn"/>.</summary>
    public bool HasSpawned => _spawnDecided;

    /// <summary>True when the one spawn attempt failed (<see cref="RecordSpawnFailure"/>).</summary>
    public bool SpawnFailed => _spawnFailed;

    /// <summary>How many times <see cref="DelayBeforeArmMs"/> has scheduled a pipe arm.</summary>
    public int ArmAttempts => _armAttempts;

    // [impl->REQ-SINK-LIFECYCLE]
    /// <summary>
    /// Decides whether to spawn the Helper now. Only the first call that finds spawning enabled,
    /// the executable present, and no Helper running returns <see cref="SpawnDecision.Spawn"/>;
    /// every later call returns <see cref="SpawnDecision.SkipAlreadySpawned"/>, even after a
    /// failed spawn or a Helper that has since exited. The cheaper skips (disabled, missing,
    /// already running) do not consume the one spawn, so a Helper the operator closes by hand
    /// and a config the operator flips mid-session behave the same on the next game launch.
    /// </summary>
    public SpawnDecision DecideSpawn(bool spawnEnabled, bool helperPresent, bool alreadyRunning)
    {
        if (_spawnDecided) return SpawnDecision.SkipAlreadySpawned;
        if (!spawnEnabled) return SpawnDecision.SkipDisabled;
        if (!helperPresent) return SpawnDecision.SkipMissing;
        if (alreadyRunning) return SpawnDecision.SkipAlreadyRunning;
        _spawnDecided = true;
        return SpawnDecision.Spawn;
    }

    // [impl->REQ-HAZARD-NO-GAMEPLAY-IMPACT]
    /// <summary>Records that the one spawn attempt failed. Never throws; the spawn stays consumed.</summary>
    public void RecordSpawnFailure()
    {
        _spawnFailed = true;
    }

    // [impl->REQ-SINK-LIFECYCLE]
    /// <summary>
    /// Called by the pump right before it arms the pipe server. Returns how long to wait first so
    /// that consecutive arms are at least the retry interval apart, and books the arm at that
    /// future instant. The first arm and any arm after a quiet stretch wait zero.
    /// </summary>
    public double DelayBeforeArmMs(double nowMs)
    {
        var delay = Math.Max(0, _retryIntervalMs - (nowMs - _lastArmMs));
        if (double.IsInfinity(delay) || double.IsNaN(delay)) delay = 0;
        _lastArmMs = nowMs + delay;
        _armAttempts++;
        return delay;
    }
}
