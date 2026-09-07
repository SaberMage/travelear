# M3 — Mixer Stage, Megaphone, Offset display, Sink feed point (JIT plan)

> Third milestone. M2 made the Clean path sound like the remote-player path, gated it to what
> peers receive, measured Offset and hardened the Helper (`M2-PLAN.md`, status log runs 1-6).
> M3 finishes v1.0 (`docs/DESIGN.md` "Scope"): the Mixer Stage, Megaphone Voice, the Offset
> row in the game's settings, and a first release. It opens with a spike on where the Sink is
> fed from, because M2 run 6 showed the game's audio thread is only a DSP host for us and a
> flaky one. Vocabulary: `CONTEXT.md`. Gate: `pwsh scripts/gates.ps1` green with the M3
> requirements activated.

## Scope

In: T0 spike (what `LocalVoicePlayer`/`SelfEcho` already wire; whether the Sink should be fed
from the encoder thread instead of the VoicePlayer path); Mixer Stage re-synthesis (M2 T4,
deferred); Megaphone Voice; the read-only Offset row and ModSettingsMenu surfacing; release
packaging and the first GitHub Release.

Out: cliff echo and water-depth muffle (v1.1); walkie-talkie and radio (operator ruling);
Thunderstore packaging (runbook says later); anything that changes what peers receive.

## Carried over from M2

- The game's audio thread stalls ~250 ms about 80 s into a session (M2 runs 5-6, in the game's
  own output too). Local Voice inherits it through the VoicePlayer path; peers do not, because
  the encoder runs on the mic thread. Question 1 below.
- `Offset:` baseline 320-360 ms with the Helper's 100 ms prime; roughly 150-200 ms of it is
  the provider ring lag the VoicePlayer path needs. Question 1 again.
- `Fidelity.SelfEarEqDryWet` stays a knob (M2 question 3); `Ear.SelfEarAngleDegrees` was never
  exposed because the Self-Ear evaluates the curves at their left edge. Revisit only if a
  second-client recording says the dry voice is wrong.
- `REQ-EAR-SELF` stays doc + impl (pinned emitter); curves/outdoorness were not needed for the
  Clean path. `REQ-OFFSET-MEASURE` still owes the pause-menu row (T3 here).

## Requirements activated by M3

Each is activated (`required_stages` set) in the commit that starts its task, per
`traceable-reqs.toml`; stages are added as evidence lands.

- `REQ-MIXER-RESYNTH` — doc + impl + unit (T1; the Core DSP is pure and testable).
- `REQ-RENDER-MEGAPHONE` — doc + impl (T2; unit for any pure DSP).
- `REQ-CONFIG-BEPINEX` — doc + impl (T3; every setting is a `ConfigEntry`, which is already true,
  plus the ModSettingsMenu check).
- `REQ-OFFSET-MEASURE` — no new stage; the row is the doc's "where supported" clause (T3).
- Hazards: `REQ-HAZARD-NO-GAME-AUDIO-LEAK` re-verified if T0 changes the Sink feed point (the
  tap's zeroing is what proves it today).

## Open design questions

1. **Where is the Sink fed from?** Today: encoder thread -> provider ring -> game `VoicePlayer`
   (synth mode, our filters) -> Tap on the Unity audio thread -> Sink. The VoicePlayer path
   contributes: Unity's spatial gain at the Self-Ear (distance ~0, angle 0: near unity), the
   optional game EQ component, and, in the plan so far, the Mixer Stage (ADR-0002 already
   puts that in mod DSP). It costs the ring lag (150-200 ms of Offset), the read-head
   discipline and its resyncs, and the game's audio-thread stalls. Alternative: process on the
   encoder thread (dynamics already run there; add the Mixer Stage DSP and, if kept, a Core
   port of `BiquadFilters` PeakingEQ) and send frames to the Sink directly; keep the in-game
   VoicePlayer only if something still needs the game's DSP. Answer by the T0 spike: list
   exactly what the VoicePlayer path adds that Core cannot reproduce; if nothing material, ADR-0005
   moves the feed point and marks the affected parts of ADR-0003 superseded. Hazard 1.1 (no
   game-audio leak) becomes trivially true with no in-game player at all.
2. **What do `LocalVoicePlayer` and `SelfEcho` already wire?** `SelfEcho` (the cliff echo) holds
   three `LocalVoicePlayer` emitters (center/left/right) fed from the raw mic provider, delay
   and decay parameters on an `AudioMixer` (`DELAY_*`, `DECAY_*`, `MASTER_VOL`), a dynamic
   reverb and an echo amount from the world. Dump both with the Cpp2IL recipe and record: the
   mixer group and floats they drive, whether `LocalVoicePlayer` is a better host than a
   generic `VoicePlayer` (if question 1 keeps one), and the delay/decay constants for v1.1's
   cliff echo.
3. **Megaphone state.** The `MegaphoneA-C` and `MegaphoneSecretZone` rooms are Open-mode token
   triggers (M2 run 2), so holding the megaphone opens a non-`Echo` room and the transmit
   signal already passes. What remains: which game state says "holding" (the token, an item
   slot on `PlayerCharacter`, or the trigger's own token check), and which mixer floats the
   remote side applies (`Megaphone{1-4}Wet/Dry`, the HP/LP pair). Answer from the decomp of
   the trigger token and `PlayerVoicePlaybackControl`.
4. **Reverb approximation and calibration.** ADR-0002 says approximate and calibrate against a
   second-client recording. No such recording exists. Plan: ship a Schroeder-style reverb with
   the two sends (`ReverbFallWet`, `ReverbBoostWet`) mapped to wet gains, each toggleable, and
   ask the operator for one two-client session before v1.0 is tagged; until then the reverb is
   marked approximate in the settings page. If a second client cannot be arranged, v1.0 ships
   with the reverb default off and the dry/high trims on.
5. **Offset row.** `SettingsRow` cloning into the game's Audio category (DESIGN "Config and
   settings UI") is unverified. Try it in T3 behind a try/catch that logs once; fallback is the
   ModSettingsMenu-surfaced read-only entry plus the log line that already exists.

## Tasks

### T0 — Spike: Sink feed point and the game's local-voice wiring

- Regenerate Cpp2IL bodies for `LocalVoicePlayer`, `SelfEcho`, `VoicePlayer.PlayVoice`,
  `AudioSourceController` (mixer routing) and the megaphone trigger token check (memory
  recipe, ~25 s); record "T0 bodies read" in this plan the way M2 did for T1.
- Answer questions 1-3 in writing here. If question 1 moves the feed point: ADR-0005, DESIGN
  "Pipeline" and "Local Voice renderer" updated, `REQ-TAP-DIVERT` re-scoped (the tap may go),
  and a one-run operator check that Offset drops by the ring lag and the 80 s stall no longer
  reaches the Sink.
- Tag: none new; docs only unless the feed point moves.

### T1 — Mixer Stage re-synthesis

- Per ADR-0002 and question 1's answer: read `Dry{n}`, `High{n}`, `ReverbFallWet{n}`,
  `ReverbBoostWet{n}` for the local player's channel each tick (`AudioMixer.GetFloat`; the
  channel index comes from the game's pooled-channel bookkeeping, read never written);
  Core DSP: dry gain, high shelf (port the game's filter constants), reverb (question 4);
  each under `Fidelity.Mixer*` toggles, `Fidelity.MixerStage` as the master.
- Unit tests against the constants; a null-mixer test (floats unreadable => stage bypassed,
  logged once, `REQ-HAZARD-NO-PARTIAL-FIDELITY`).
- Operator verification: A/B each toggle; the two-client recording if it exists.
- Tag: `[doc->REQ-MIXER-RESYNTH]` in DESIGN (Mixer Stage section), `[impl->…]`, `[unit->…]`.

### T2 — Megaphone Voice

- Per question 3: detect "holding" on the main thread; switch the Filter/Mixer inputs to the
  megaphone set while held and back to Clean when dropped, with the game's own transition (if
  the remote side crossfades, match it; if it hard-switches, hard-switch).
- Operator verification: pick up the megaphone in a solo session, speak, drop it; the Sink
  follows within a frame; the log shows the switch.
- Tag: `[doc->REQ-RENDER-MEGAPHONE]`, `[impl->…]`.

### T3 — Offset row and settings surfacing

- Read-only "TravelEar offset: N ms" row (question 5) from `OffsetMonitor.LastAverageMs`,
  refreshed every 10 s; ModSettingsMenu check: every `Fidelity.*`, `Sink.*`, `Ear.*` entry
  visible with its description; docs-site settings page kept in step.
- Tag: `[doc->REQ-CONFIG-BEPINEX]` (DESIGN Config section), `[impl->…]`; `REQ-OFFSET-MEASURE`
  doc line for the row.

### T4 — Release v1.0

- Per `docs/RELEASE-RUNBOOK.md`: gates, CHANGELOG cut, version bump, tag, local Release build
  of plugin + Core + Helper, `gh release create` with the zip and a README section on install,
  `SinkEndpoint`, OBS setup (Application Audio Capture of "TravelEar for Big Walk"), and the
  Offset figure.
- Operator verification: fresh install from the release zip into a clean BepInEx, one solo run.

### T5 — Close-out

- Requirement stages final; status log complete; M3 outcome section; M4-PLAN seeds (v1.1
  cliff echo from question 2's constants, water muffle, Thunderstore).

## Status log

(empty; entries are added as tasks start and land, newest first is not required, keep them in
task order)

## Gate

`pwsh scripts/gates.ps1` green; every task's operator verification recorded here; the v1.0 tag
exists and the GitHub Release carries the zip.
