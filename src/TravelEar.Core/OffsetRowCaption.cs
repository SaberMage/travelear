using System.Globalization;

namespace TravelEar.Core;

/// <summary>
/// Caption of the read-only Offset row in the game's Audio settings (<c>REQ-OFFSET-MEASURE</c>,
/// docs/DESIGN.md "Config and settings UI"): the rolling 10 s average from
/// <see cref="OffsetAverager"/> as "TravelEar offset: N ms", or "measuring" until there is one.
/// Pure and culture-invariant, so the plugin can format it on the main thread.
/// </summary>
public static class OffsetRowCaption
{
    public const string Prefix = "TravelEar offset: ";

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>"TravelEar offset: N ms" for a finite, non-negative average; "TravelEar offset: measuring" otherwise (NaN before the first average).</summary>
    public static string Format(double averageMs)
    {
        if (double.IsNaN(averageMs) || double.IsInfinity(averageMs) || averageMs < 0)
            return Prefix + "measuring";
        return Prefix + averageMs.ToString("F0", CultureInfo.InvariantCulture) + " ms";
    }
}
