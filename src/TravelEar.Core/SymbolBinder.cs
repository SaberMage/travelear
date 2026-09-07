namespace TravelEar.Core;

/// <summary>
/// The bind step's bookkeeping (docs/KNOWN-HAZARDS.md 3.1, <c>REQ-HAZARD-NO-PARTIAL-FIDELITY</c>):
/// the mod resolves every game symbol it will touch through one binder before installing any
/// hook; if a single symbol is missing the whole mod stays off and says so in one log line that
/// names every miss. Resolution is injected (<see cref="Resolve{T}"/> takes the lookup), so the
/// all-or-nothing rule is unit-tested without the game. A lookup that throws counts as missing.
/// </summary>
public sealed class SymbolBinder
{
    private readonly List<string> _missing = new();
    private int _resolved;

    /// <summary>Symbols that failed to resolve so far, in order.</summary>
    public IReadOnlyList<string> Missing => _missing;

    /// <summary>Symbols that resolved so far.</summary>
    public int Resolved => _resolved;

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Resolves one symbol. Returns the lookup's result, or null (and records
    /// <paramref name="symbol"/> as missing) when the lookup returns null or throws.
    /// </summary>
    public T Resolve<T>(string symbol, Func<T> lookup) where T : class
    {
        T result = null;
        try
        {
            result = lookup();
        }
        catch (Exception)
        {
            // Ambiguous, unloadable, or otherwise unresolvable: missing, never fatal here.
        }
        if (result is null) _missing.Add(symbol);
        else _resolved++;
        return result;
    }

    /// <summary>Records a symbol as present or missing when the caller already knows.</summary>
    public void Require(string symbol, bool present)
    {
        if (present) _resolved++;
        else _missing.Add(symbol);
    }

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// The verdict: true when every symbol resolved. <paramref name="report"/> is the single log
    /// line to emit either way; with misses it is an error line naming each of them.
    /// </summary>
    public bool Complete(out string report)
    {
        if (_missing.Count > 0)
        {
            report = $"TravelEar disabled: {_missing.Count} game symbol(s) not found after a game update: {string.Join(", ", _missing)}";
            return false;
        }
        report = $"Game symbols bound ({_resolved}).";
        return true;
    }
}
