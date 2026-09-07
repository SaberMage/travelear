---
status: accepted
---

# Mixer Stage effects are re-synthesized in the mod, not captured from the engine

Big Walk applies its voice processing in two stages: a per-voice in-process filter chain (capturable via the source's audio-filter callback) and Unity AudioMixer effects driven by per-channel floats the game sets every frame (reverb sends, dry/high trims, megaphone wet/dry, walkie low-pass, compressors). Unity exposes no way to read a mixer group's output, and the mod cannot add mixer effects or a second listener at runtime, so exact capture of the Mixer Stage is impossible. We chose to capture the Filter Stage exactly and re-implement the Mixer Stage in the mod's own DSP, driven by the same live mixer floats the game writes, accepting that reverb is an approximation calibrated by ear against real second-client recordings.

## Considered options

- Filter Stage only. Rejected: loses reverb and megaphone character, the parts that most define "how others hear you".
- Route Local Voice through the real mixer and let it play. Rejected: the user would hear themselves and OBS's game track would contain it.
- Synthetic remote player riding the game's full playback path. Kept as fallback only; it still cannot tap post-mixer audio and adds a fake network identity.

## Consequences

- Every Mixer Stage effect is individually toggleable in config so it can be A/B'd against a real remote recording.
- Game updates that rename mixer parameters break the Mixer Stage; the mod must hard-fail and log rather than silently emit Filter-Stage-only audio.
