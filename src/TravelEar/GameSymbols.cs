using System.Reflection;
using BepInEx.Logging;
using Dissonance.Audio.Codecs.Opus;

namespace TravelEar;

/// <summary>
/// Every game class and method the mod touches, resolved once at load. Names are facts from the
/// decompiled game (see AGENTS.md); if any is missing after a game update the whole mod stays
/// off and says so in one log line, instead of running with whatever still binds.
/// </summary>
internal static class GameSymbols
{
    /// <summary><c>OpusEncoder.Encode(ArraySegment&lt;float&gt;, ArraySegment&lt;byte&gt;)</c>: produces every Outbound Voice frame.</summary>
    public static MethodInfo OpusEncoderEncode { get; private set; }

    public static bool IsBound { get; private set; }

    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Resolves all symbols. Returns false, having logged the complete list of misses, if any
    /// symbol is absent. Callers must not install any hook when this returns false.
    /// </summary>
    public static bool Bind(ManualLogSource log)
    {
        var missing = new List<string>();

        OpusEncoderEncode = Method(missing, typeof(OpusEncoder), "Encode",
            typeof(Il2CppSystem.ArraySegment<float>), typeof(Il2CppSystem.ArraySegment<byte>));

        if (missing.Count > 0)
        {
            IsBound = false;
            log.LogError($"TravelEar disabled: {missing.Count} game symbol(s) not found after a game update: {string.Join(", ", missing)}");
            return false;
        }

        IsBound = true;
        log.LogInfo("Game symbols bound.");
        return true;
    }

    private static MethodInfo Method(List<string> missing, Type type, string name, params Type[] parameters)
    {
        MethodInfo method = null;
        try
        {
            method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, parameters, null);
        }
        catch (Exception)
        {
            // Ambiguous or otherwise unresolvable: treated as missing below.
        }
        if (method is null)
            missing.Add($"{type.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})");
        return method;
    }
}
