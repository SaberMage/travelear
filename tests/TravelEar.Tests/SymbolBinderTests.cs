using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class SymbolBinderTests
{
    [Fact]
    public void Binds_when_every_symbol_resolves()
    {
        var binder = new SymbolBinder();
        Assert.Equal("a", binder.Resolve("Game.A", () => "a"));
        Assert.Equal("b", binder.Resolve("Game.B", () => "b"));
        binder.Require("Game.C", present: true);
        Assert.True(binder.Complete(out var report));
        Assert.Equal("Game symbols bound (3).", report);
        Assert.Empty(binder.Missing);
    }

    // [unit->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    [Fact]
    public void One_missing_symbol_disables_the_bind_and_names_it_in_one_line()
    {
        var binder = new SymbolBinder();
        binder.Resolve("Game.OpusEncoder.Encode", () => "ok");
        Assert.Null(binder.Resolve<string>("Game.AudioFilterMixer.OnAudioFilterRead", () => null));
        binder.Resolve("Game.VoicePlayer.Cue", () => "ok");

        Assert.False(binder.Complete(out var report));
        Assert.Contains("TravelEar disabled", report);
        Assert.Contains("1 game symbol(s)", report);
        Assert.Contains("Game.AudioFilterMixer.OnAudioFilterRead", report);
        Assert.DoesNotContain("\n", report); // exactly one log line
        Assert.Equal(new[] { "Game.AudioFilterMixer.OnAudioFilterRead" }, binder.Missing);
    }

    // [unit->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    [Fact]
    public void A_lookup_that_throws_counts_as_missing_and_never_escapes()
    {
        var binder = new SymbolBinder();
        var ex = Record.Exception(() => binder.Resolve<string>("Game.Ambiguous", () => throw new System.Reflection.AmbiguousMatchException()));
        Assert.Null(ex);
        Assert.False(binder.Complete(out var report));
        Assert.Contains("Game.Ambiguous", report);
    }

    // [unit->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    [Fact]
    public void Every_miss_is_listed_so_one_log_line_is_enough()
    {
        var binder = new SymbolBinder();
        binder.Require("Game.X", false);
        binder.Resolve<string>("Game.Y", () => null);
        binder.Require("Game.Z", true);
        Assert.False(binder.Complete(out var report));
        Assert.Contains("2 game symbol(s)", report);
        Assert.Contains("Game.X, Game.Y", report);
        Assert.DoesNotContain("Game.Z", report);
    }
}
