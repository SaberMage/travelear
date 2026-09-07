# Remote-path voice processing is ported into Core, not borrowed from the game's components

## Status

accepted (2026-09-07)

## Context

1. ADR-0003 left the Clean **Local Voice** path as the game's own self-voice chain, which lacks
   everything a remote player's voice gets on the way to the speakers: `SamplePlaybackComponent`'s
   per-sample gain ramp, `VoiceCompressor`, soft clip and ARV metering; the per-player
   `VoiceMakeupGain` automatic gain control; and `PlayerVoicePlaybackControl`'s distance/angle
   curves driving a `BiquadFilters` peaking EQ. Without them Local Voice sits at a different
   level from the peers' voices and peaks hit the hard clamp instead of the 0.95 soft ceiling
   (`REQ-RENDER-CLEAN`, `REQ-EAR-SELF`).
2. ADR-0003's rule of thumb prefers owning an instance of a game type over reimplementing it.
   The Cpp2IL read (M2-PLAN "T1 bodies read", `docs/reference/big-walk-voice-dsp.md`) showed
   why that does not fit here: `SamplePlaybackComponent` is the remote provider itself (its
   processing is inseparable from its network-fed ring); `VoiceMakeupGain` is a static dictionary
   keyed by player name whose `TargetARV` and `VoiceCompressor.Threshold` are shared with every
   remote voice; the EQ's dry/wet comes from curve assets on a prefab only a remote player
   instantiates, and at the Self-Ear (distance 0, angle 0) both curve inputs sit at their left
   edge.
3. The DSP is small and fully specified by the read: compressor threshold `min(TargetARV x 4,
   0.6)`, knee 0.4 x threshold, 5 ms attack, 150 ms release, ratio 2; soft clip knee 0.85,
   headroom 0.1, asymptote 0.95; makeup gain toward ARV 0.132 at 24 dB/s for the first second of
   speech, then 12 dB/s up and 1 dB/s down, frozen between bursts.

## Decision

The remote path's processing is **ported into `TravelEar.Core`** as pure, unit-tested DSP:
`VoiceDynamics` (gain ramp, `VoiceCompressor`, soft clip, meters) runs on the encoder thread
over each decoded frame before the provider push; `VoiceMakeupGain` runs once per frame on the
main thread from the last block's input level. The game's `VoiceMakeupGain.TargetARV` and
`VoiceCompressor.Threshold` statics are **read, never written**, so the in-game voice volume
slider couples to Local Voice exactly as it does to peers. A burst start resets the dynamics the
way a new speech session resets a remote voice; the makeup-gain state persists across bursts.

The one game component that is reused is `BiquadFilters`: attached to the pooled source only
when config `Fidelity.SelfEarEqDryWet` is above 0, configured as `PlayVoice` does (PeakingEQ 400
Hz, Q 0.3, +30 dB, Vol 0.03) with the wet mix pinned to the config value instead of the curves.

Rejected: registering the mod's voice in the game's `VoiceMakeupGain` dictionary under a
synthetic name (shares state the game owns, and a game update to the keying breaks it
silently); driving a `PlayerVoicePlaybackControl` for the Self-Ear (it also writes the pooled
channel's mixer floats, which belong to the Mixer Stage and to other players' channels).

## Consequences

- Core now carries game-derived constants that must be re-read against the game's bodies after
  an update (`docs/reference/big-walk-voice-dsp.md` records where each came from; the Cpp2IL
  recipe regenerates them in about 25 s). A drift shows up as a level mismatch, not a crash.
- The unit tests pin the port to the constants, not to a recording: the operator's A/B against
  a second-client recording is still the fidelity check that matters.
- The Self-Ear stays dry by default; `SelfEarEqDryWet` is a calibration knob until a
  second-client recording settles the off-axis question (M2-PLAN question 3).
- The rule of thumb from ADR-0003 gains a clause: reuse a game type when the mod can own an
  instance whose state is private to it; port when the game's instance is shared, static, or
  welded to a provider the mod does not use.
