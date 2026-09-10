namespace TravelEar.Core;

/// <summary>
/// The listener-side gain terms outside the mixer stages (docs/reference/big-walk-voice-effects-catalog.md):
/// the source volume chain's indoor attenuation and speechlessness fade (<see cref="SourceVolume"/>)
/// the main mixer's master limiter (<see cref="MasterLimiter"/>), and the red bells' listener-side
/// pair: the voice pitch (<see cref="MixerStageToggles.Pitch"/> carries it into the mixer stage)
/// and the super-wet bloom (<see cref="SpeechlessBloom"/>). Config <c>Fidelity.IndoorAttenuation</c>,
/// <c>Fidelity.SpeechlessVolume</c>, <c>Fidelity.MasterLimiter</c>, <c>Fidelity.SpeechlessPitch</c>,
/// <c>Fidelity.SpeechlessBloom</c>.
/// </summary>
public readonly record struct ListenerToggles(bool IndoorAttenuation, bool SpeechlessVolume, bool MasterLimiter, bool SpeechlessPitch = true, bool SpeechlessBloom = true)
{
    public static ListenerToggles All => new(true, true, true, true, true);
    public static ListenerToggles Off => new(false, false, false, false, false);
}
