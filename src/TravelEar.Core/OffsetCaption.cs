using System.Globalization;

namespace TravelEar.Core;

/// <summary>
/// The Offset caption the Helper's window shows (<c>REQ-OFFSET-MEASURE</c>, docs/DESIGN.md
/// "Offset"): the rolling 10 s average from <see cref="OffsetAverager"/> as
/// "TravelEar offset: N ms", or "measuring" until there is one. Pure and culture-invariant.
/// </summary>
public static class OffsetCaption
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
