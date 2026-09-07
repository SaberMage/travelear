# Mixer Stage effects are re-synthesized in the mod, not captured from the engine

## Status

accepted (2026-09-06)

## Context

1. Big Walk applies voice processing in two stages: a per-voice in-process **Filter Stage**
   (capturable via the source's audio-filter callback) and a **Mixer Stage** of Unity
   AudioMixer effects driven by per-channel floats the game sets every frame (reverb sends,
   dry/high trims, megaphone wet/dry, walkie low-pass, compressors).
2. Unity exposes no API to read a mixer group's output, cannot add mixer effects at runtime,
   and allows one AudioListener. Exact capture of the Mixer Stage is impossible in-process.
3. The Mixer Stage carries the reverb and megaphone character that most define "how others hear
   you", so dropping it is not acceptable.

## Decision

Capture the Filter Stage exactly at the **Tap** and re-implement the Mixer Stage in the mod's
own DSP, driven by the same live mixer floats the game writes (`AudioMixer.GetFloat`). Reverb is
an approximation, calibrated by ear against recordings made on a real second client. Each
re-synthesized effect is individually toggleable in config for A/B testing.

Rejected: routing Local Voice through the real mixer and letting it play (the user would hear
themselves and OBS's game track would contain it); a synthetic remote player riding the game's
full playback path (kept only as a fallback; it still cannot tap post-mixer audio and adds a fake
network identity).

## Consequences

- Fidelity is a ladder, documented in the README: Filter Stage exact, Mixer Stage approximate.
- Game updates that rename mixer parameters break the Mixer Stage; the mod hard-fails and logs
  rather than emitting Filter-Stage-only audio (`REQ-HAZARD-NO-PARTIAL-FIDELITY`).
- A second-client calibration recording is a standing test asset.
