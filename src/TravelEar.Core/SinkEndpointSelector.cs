namespace TravelEar.Core;

/// <summary>
/// Picks the WASAPI render endpoint for the Sink from the user's <c>SinkEndpoint</c> setting.
/// Pure: the Helper enumerates device friendly names and passes them in.
/// </summary>
public static class SinkEndpointSelector
{
    /// <summary>Index value meaning "use the system default render device".</summary>
    public const int DefaultDevice = -1;

    // [impl->REQ-SINK-ENDPOINT-CONFIG]
    /// <summary>
    /// Resolves <paramref name="setting"/> against <paramref name="friendlyNames"/> (enumeration
    /// order). Empty or whitespace = <see cref="DefaultDevice"/>. Otherwise an exact
    /// case-insensitive name match wins, then the first case-insensitive substring match.
    /// Returns false when nothing matches, so the caller can refuse to render to a guess.
    /// </summary>
    public static bool TryChoose(IReadOnlyList<string> friendlyNames, string? setting, out int index)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            index = DefaultDevice;
            return true;
        }

        var wanted = setting.Trim();
        for (var i = 0; i < friendlyNames.Count; i++)
        {
            if (string.Equals(friendlyNames[i], wanted, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }
        for (var i = 0; i < friendlyNames.Count; i++)
        {
            if (friendlyNames[i].Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        index = DefaultDevice;
        return false;
    }
}
