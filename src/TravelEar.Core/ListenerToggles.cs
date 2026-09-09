namespace TravelEar.Core;

/// <summary>
/// The listener-side gain terms outside the mixer stages (docs/reference/big-walk-voice-effects-catalog.md):
/// the source volume chain's indoor attenuation and speechlessness fade (<see cref="SourceVolume"/>)
/// and the main mixer's master limiter (<see cref="MasterLimiter"/>). Config <c>Fidelity.IndoorAttenuation</c>,
/// <c>Fidelity.SpeechlessVolume</c>, <c>Fidelity.MasterLimiter</c>.
/// </summary>
public readonly record struct ListenerToggles(bool IndoorAttenuation, bool SpeechlessVolume, bool MasterLimiter)
{
    public static ListenerToggles All => new(true, true, true);
    public static ListenerToggles Off => new(false, false, false);
}
