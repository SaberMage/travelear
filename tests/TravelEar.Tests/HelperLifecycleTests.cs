using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class HelperLifecycleTests
{
    // [unit->REQ-SINK-LIFECYCLE]
    [Fact]
    public void Spawns_exactly_once_per_session()
    {
        var life = new HelperLifecycle();
        Assert.Equal(SpawnDecision.Spawn, life.DecideSpawn(spawnEnabled: true, helperPresent: true, alreadyRunning: false));
        Assert.True(life.HasSpawned);
        // The Helper exiting later (alreadyRunning false again) does not earn a respawn.
        Assert.Equal(SpawnDecision.SkipAlreadySpawned, life.DecideSpawn(true, true, false));
        Assert.Equal(SpawnDecision.SkipAlreadySpawned, life.DecideSpawn(true, true, false));
    }

    [Fact]
    public void Skips_when_disabled_missing_or_already_running_without_consuming_the_spawn()
    {
        var life = new HelperLifecycle();
        Assert.Equal(SpawnDecision.SkipDisabled, life.DecideSpawn(false, true, false));
        Assert.Equal(SpawnDecision.SkipMissing, life.DecideSpawn(true, false, false));
        Assert.Equal(SpawnDecision.SkipAlreadyRunning, life.DecideSpawn(true, true, true));
        Assert.False(life.HasSpawned);
        Assert.Equal(SpawnDecision.Spawn, life.DecideSpawn(true, true, false));
    }

    // [unit->REQ-HAZARD-NO-GAMEPLAY-IMPACT]
    [Fact]
    public void A_failed_spawn_is_absorbed_and_never_retried()
    {
        var life = new HelperLifecycle();
        Assert.Equal(SpawnDecision.Spawn, life.DecideSpawn(true, true, false));
        var ex = Record.Exception(() => life.RecordSpawnFailure());
        Assert.Null(ex);
        Assert.True(life.SpawnFailed);
        Assert.Equal(SpawnDecision.SkipAlreadySpawned, life.DecideSpawn(true, true, false));
    }

    // [unit->REQ-SINK-LIFECYCLE]
    [Fact]
    public void First_arm_waits_zero_and_rapid_disconnects_are_spaced_by_the_retry_interval()
    {
        var life = new HelperLifecycle(retryIntervalMs: 5000);
        Assert.Equal(0, life.DelayBeforeArmMs(nowMs: 0));
        // Helper connected and vanished 200 ms later: wait the rest of the interval.
        Assert.Equal(4800, life.DelayBeforeArmMs(200));
        // That arm is booked at t=5000; another disconnect right after it waits a full interval.
        Assert.Equal(5000, life.DelayBeforeArmMs(5000));
        Assert.Equal(3, life.ArmAttempts);
    }

    [Fact]
    public void An_arm_after_a_quiet_stretch_waits_zero()
    {
        var life = new HelperLifecycle(5000);
        life.DelayBeforeArmMs(0);
        Assert.Equal(0, life.DelayBeforeArmMs(60_000));
    }

    // [unit->REQ-HAZARD-NO-GAMEPLAY-IMPACT]
    [Fact]
    public void Arms_are_never_closer_than_the_retry_interval_however_fast_the_pipe_closes()
    {
        const double interval = 5000;
        var life = new HelperLifecycle(interval);
        var random = new Random(20260907);
        var now = 0.0;
        var lastArm = double.NegativeInfinity;
        for (var i = 0; i < 1000; i++)
        {
            now += random.Next(0, 3000); // the pipe closes 0-3 s after each arm
            var delay = life.DelayBeforeArmMs(now);
            var arm = now + delay;
            Assert.True(delay >= 0);
            Assert.True(arm - lastArm >= interval, $"arm {i} at {arm} follows {lastArm} by less than {interval} ms");
            lastArm = arm;
            now = arm;
        }
        Assert.Equal(1000, life.ArmAttempts);
    }

    [Fact]
    public void A_zero_interval_never_waits()
    {
        var life = new HelperLifecycle(0);
        Assert.Equal(0, life.DelayBeforeArmMs(0));
        Assert.Equal(0, life.DelayBeforeArmMs(0));
        Assert.Equal(0, life.DelayBeforeArmMs(1));
    }

    [Fact]
    public void Rejects_a_negative_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HelperLifecycle(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HelperLifecycle(double.NaN));
    }
}
