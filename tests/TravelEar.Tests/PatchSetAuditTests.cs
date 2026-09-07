using System.Text.RegularExpressions;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// docs/KNOWN-HAZARDS.md 4.1 (<c>REQ-HAZARD-NO-PEER-SURFACE</c>): the mod only observes. Its
/// Harmony patch set may contain postfixes, plus exactly one whitelisted prefix (the mic
/// subscription skip keyed to the mod-owned provider instance). Anything that could alter what
/// the game sends or who it talks to (a prefix elsewhere, a transpiler, a reverse patch, a finalizer)
/// is a failure. The plugin assembly cannot load in a test process (IL2CPP interop), so this
/// audits the source.
/// </summary>
public class PatchSetAuditTests
{
    private static readonly (string File, string Method)[] AllowedPrefixes =
    {
        ("RoundTripProvider.cs", "SkipMicSubscription"),
    };

    private static string PluginSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "traceable-reqs.toml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "TravelEar");
    }

    private static IEnumerable<(string Name, string Text)> PluginSources() =>
        Directory.EnumerateFiles(PluginSourceDir(), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    // [unit->REQ-HAZARD-NO-PEER-SURFACE]
    [Fact]
    public void Only_postfixes_and_the_one_whitelisted_prefix_are_patched()
    {
        var sources = PluginSources().ToList();
        Assert.NotEmpty(sources);

        var prefixes = new List<(string File, string Method)>();
        var postfixes = 0;
        foreach (var (name, text) in sources)
        {
            // A prefix attribute followed (after modifiers and return type) by the method name.
            foreach (Match m in Regex.Matches(text, @"\[HarmonyPrefix\]\s*(?:\[[^\]]*\]\s*)*(?:private|internal|public|static|\s)*\s*\w+\s+(\w+)\s*\("))
                prefixes.Add((name, m.Groups[1].Value));
            postfixes += Regex.Matches(text, @"\[HarmonyPostfix\]").Count;

            Assert.DoesNotContain("HarmonyTranspiler", text);
            Assert.DoesNotContain("HarmonyReversePatch", text);
            Assert.DoesNotContain("HarmonyFinalizer", text);
        }

        Assert.True(postfixes > 0, "expected at least one postfix (the Outbound Voice tap)");
        Assert.Equal(AllowedPrefixes.OrderBy(p => p.File), prefixes.OrderBy(p => p.File));
    }

    // [unit->REQ-HAZARD-NO-PEER-SURFACE]
    [Fact]
    public void The_whitelisted_prefix_lets_the_games_own_instances_run_unchanged()
    {
        var text = PluginSources().Single(s => s.Name == "RoundTripProvider.cs").Text;
        // The skip is keyed to the mod-owned pointer and returns true (run the original) otherwise.
        Assert.Matches(@"__instance\.Pointer\s*!=\s*Interlocked\.Read\(ref _ownedPointer\)\)\s*return true;", text);
    }

    // [unit->REQ-HAZARD-NO-PEER-SURFACE]
    [Fact]
    public void No_plugin_source_sends_on_the_network_or_joins_a_room()
    {
        foreach (var (name, text) in PluginSources())
        {
            // Code only: the tap's doc comment explains why the send path was rejected.
            var code = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
            Assert.DoesNotMatch(@"\bSendVoiceData\b", code);
            Assert.DoesNotMatch(@"\.JoinRoom\(|\.LeaveRoom\(|\bRoomChannels\.Open\(|\bPlayerChannels\.Open\(", code);
            Assert.DoesNotMatch(@"\bNetworkServer\.|\bNetworkClient\.|\bClientScene\.", code);
        }
    }
}
