using System;
using System.Threading;
using HarmonyLib;
using System.Reflection;

namespace TravelEar;

/// <summary>
/// The main mixer's exposed floats the renderer needs but cannot read: a Harmony postfix on
/// <c>AudioMixer.SetFloat(string, float)</c> that remembers the game's own writes.
/// <para>
/// <c>AudioMixer.GetFloat(string, out float)</c> is stripped from this game and comes back
/// through Il2CppInterop's unstripping with Unity 6's managed body, which pins the name as an
/// <c>Il2CppSystem.ReadOnlySpan&lt;char&gt;</c> whose <c>GetPinnableReference</c> is stripped too:
/// every call throws <c>MissingMethodException</c> (M3 run 3). The native
/// <c>GetFloat_Injected</c> icall is not registered in GameAssembly.dll either, so there is no
/// read path at all. <c>SetFloat(string, float)</c> is a real IL2CPP method the game calls, so
/// its detour sees every value the game writes.
/// </para>
/// <para>
/// Until the game writes a watched parameter its value is unknown; the reader reports that and
/// the caller assumes the mixer asset's nominal 0 dB, logged once, rather than bypassing the
/// stage for a parameter the game may only touch on a settings change.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class MixerFloats
{
    /// <summary>The Master Wet return bus volume, dB. The environment reverb's return level.</summary>
    public const string MasterWet = "MasterWet";

    private static int _masterWetWritten;
    private static float _masterWetDb;
    private static long _masterWetWrites;

    /// <summary>Number of <c>MasterWet</c> writes the game has made since load.</summary>
    public static long MasterWetWrites => Interlocked.Read(ref _masterWetWrites);

    /// <summary>
    /// The last <c>MasterWet</c> value the game wrote. False (and 0 dB) while the game has not
    /// written it yet; the caller decides what to assume and says so.
    /// </summary>
    public static bool TryGetMasterWet(out float db)
    {
        if (Volatile.Read(ref _masterWetWritten) != 0)
        {
            db = Volatile.Read(ref _masterWetDb);
            return true;
        }
        db = 0f;
        return false;
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.AudioMixerSetFloat;

    // [impl->REQ-MIXER-RESYNTH]
    [HarmonyPostfix]
    private static void Postfix(string name, float value, bool __result)
    {
        try
        {
            if (!__result || name != MasterWet) return;
            Volatile.Write(ref _masterWetDb, value);
            Volatile.Write(ref _masterWetWritten, 1);
            Interlocked.Increment(ref _masterWetWrites);
        }
        catch (Exception)
        {
            // Never disturb the game's mixer writes.
        }
    }
}
