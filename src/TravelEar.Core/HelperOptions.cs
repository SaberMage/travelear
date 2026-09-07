namespace TravelEar.Core;

/// <summary>
/// Command line of <c>TravelEar.Helper.exe</c>. Parsed here so the grammar is unit-tested and
/// the mod (which launches the Helper) and the Helper agree on it.
/// </summary>
/// <param name="EndpointSetting">The <c>SinkEndpoint</c> value: empty = system default render device.</param>
/// <param name="Tone">Render a test tone instead of reading the pipe (spike S1 needs no game).</param>
/// <param name="PipeName">Named pipe the mod serves Sink frames on.</param>
/// <param name="ShowHelp"><c>--help</c> was given; print usage and exit.</param>
/// <param name="Detach">Re-launch self without this flag and exit at once, so the real Helper's parent is this short-lived launcher rather than whoever started it (the game).</param>
public sealed record HelperOptions(string EndpointSetting, bool Tone, string PipeName, bool ShowHelp, bool Detach = false)
{
    public const string DefaultPipeName = "TravelEar.Sink";

    public static readonly HelperOptions Default = new("", false, DefaultPipeName, false);

    public const string Usage =
        "TravelEar.Helper [--endpoint <substring>] [--tone] [--pipe <name>] [--detach] [--help]\n" +
        "  --endpoint <substring>  Render to the playback device whose name contains <substring> (empty = default device).\n" +
        "  --tone                  Render a 440 Hz test tone instead of reading the Sink pipe.\n" +
        "  --pipe <name>           Named pipe to read Sink frames from (default " + DefaultPipeName + ").\n" +
        "  --detach                Start a second copy without this flag and exit, leaving it outside the caller's process tree.\n" +
        "  --help                  Show this text.";

    /// <summary>Builds the argument vector the mod passes when launching the Helper.</summary>
    public string[] ToArgs()
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(EndpointSetting)) { args.Add("--endpoint"); args.Add(EndpointSetting); }
        if (Tone) args.Add("--tone");
        if (PipeName != DefaultPipeName) { args.Add("--pipe"); args.Add(PipeName); }
        if (Detach) args.Add("--detach");
        return args.ToArray();
    }

    /// <summary>
    /// Parses <c>--endpoint X</c> / <c>--endpoint=X</c>, <c>--tone</c>, <c>--pipe X</c>, <c>--help</c>.
    /// Throws <see cref="ArgumentException"/> on an unknown option or a missing value.
    /// </summary>
    public static HelperOptions Parse(IReadOnlyList<string> args)
    {
        var result = Default;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inlineValue = arg.Substring(eq + 1);
                arg = arg.Substring(0, eq);
            }

            switch (arg.ToLowerInvariant())
            {
                case "--endpoint":
                    result = result with { EndpointSetting = TakeValue(args, ref i, inlineValue, arg) };
                    break;
                case "--pipe":
                    var pipe = TakeValue(args, ref i, inlineValue, arg);
                    if (pipe.Length == 0) throw new ArgumentException("--pipe needs a non-empty name.");
                    result = result with { PipeName = pipe };
                    break;
                case "--tone":
                    if (inlineValue is not null) throw new ArgumentException("--tone takes no value.");
                    result = result with { Tone = true };
                    break;
                case "--detach":
                    if (inlineValue is not null) throw new ArgumentException("--detach takes no value.");
                    result = result with { Detach = true };
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result = result with { ShowHelp = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }
        return result;
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int i, string? inlineValue, string option)
    {
        if (inlineValue is not null) return inlineValue;
        if (i + 1 >= args.Count) throw new ArgumentException($"{option} needs a value.");
        i++;
        return args[i];
    }
}
