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

### T2b — Environment reverb

- Added 2026-09-08 from the operator's hallway observation (friends' voices in the starting-area
  hallway carry a room reverb; Local Voice had none). T1 modelled the per-voice sends only; the
  hallway reverb is the listener-side `Master Wet` SFX Reverb the game re-parameterizes every
  frame from `AudioDynamicReverb` (docs/reference/big-walk-environment-reverb.md). Core model of
  the fourteen parameters (read live, formulas kept as the test oracle), `SfxReverb` (I3DL2
  mapping onto the Freeverb network: dry copy, room, reflections/delay, reverb/delay, decay,
  HF ratio, two shelves, diffusion, density), `EnvironmentReverb` stage last in the encoder chain
  with the bus trims, toggles under `Fidelity.EnvironmentReverb*`, tests; symbols in
  `GameSymbols.Bind`. v1.0 must include it (operator's stated goal: the host-side output is
  exactly what others hear).
- Tag: `[doc->REQ-MIXER-RESYNTH]`, `[impl->…]`, `[unit->…]`.
- Operator verification: hallway A/B (stand where the friends' reverb was heard, talk; then
  outdoors), `Environment reverb:` log line present, stage `live` in the stats line.

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

- **T0 built** (2026-09-07): the bodies read below answered questions 1-3; question 1 moved the
  feed point (ADR-0005). Config `Fidelity.SinkFeed` (`Encoder` default, `VoicePlayer` = the
  ADR-0003 path, unchanged, for A/B). Encoder feed: the renderer creates nothing in the scene;
  each decoded frame goes gate -> fade -> `VoiceDynamics` -> Core `PeakingEq` (the game's
  `BiquadFilters` PeakingEQ kernel, wet mix from `SelfEarEqDryWet`, 6 tests) -> `SinkFeed` ring
  (mono 48 kHz, stamped with the encode time so Offset still resolves) -> pump. Helper: after its
  ring runs dry it holds on silence until the backlog is back at 100 ms (`StarveGuard`, 5 tests;
  `starves` counter in Helper.log and the status window), because the encoder stops with the mic
  and a burst from an empty ring would click on every jitter. Pump takes its stamp table from the
  feed point. Docs: ADR-0005, DESIGN (Pipeline, provider, renderer, Tap, Sink), CONTEXT (Tap,
  Feed point), KNOWN-HAZARDS 1.1, docs-site settings + how-it-works, `REQ-TAP-DIVERT` re-scoped
  to the `VoicePlayer` feed. 142 tests. Deployed. Operator check owed (one solo run on the
  default feed): Local Voice audible in the Sink with no in-game double; `Offset:` well under the
  M2 baseline of 320-360 ms (expected ~150-200 ms: Helper prime + pipe + encode hop, no ring
  lag); the ~80 s crackle absent from the Sink; `Local Voice stats` shows `encoder feed:` blocks
  climbing with `dropped 0`; Helper.log `starves` only around mutes / menus. Then, if time allows,
  a second run with `SinkFeed = VoicePlayer` to confirm the fallback still works.
- **M3 run 1** (2026-09-08 03:44-03:47 local, operator solo run on `08cee6d`, default feed):
  `Sink feed point: Encoder`, `encoder feed built; 48000 Hz mono`, 3505 frames encoded = decoded
  = sent, `dropped 0`, `underruns 0`, `errors 0`, no warnings. `Offset:` 200-209 ms rolling
  average all run (per-frame 109-254 ms), against the M2 baseline of 320-360 ms: the provider ring
  lag is gone as ADR-0005 predicted, and what remains is the Helper's 100 ms prime plus pipe and
  encode hops. Helper.log every 10 s: `underruns 0, starves 0, trims 0`, backlog 7200-8640
  samples (150-180 ms, inside the 100-200 ms band). Gate and fade behaved as in M2 (fade 0/150
  ms, hold 210 ms, 247 signal changes). Makeup gain settled 0.8-3.7 depending on level, as
  before. Operator's ear (Local Voice audible in the Sink, no in-game double, 80 s crackle
  absent): asked, verdict appended here when reported. Fallback run (`SinkFeed = VoicePlayer`) not done; deferred to T4's fresh-install
  run, since the fallback is unchanged code.
- **T1 built** (2026-09-08): the plan's premise ("read the mixer floats for the local player's
  channel") was wrong, as the M2 report's flag list already said: the game writes `Dry{n}` /
  `High{n}` / `ReverbFallWet{n}` / `ReverbBoostWet{n}` only from a `PlayerVoicePlaybackControl`,
  which exists per remote voice on a listener's machine, never for the local player. ADR-0002
  amended: Core `MixerStageModel` ports the `Update` formulas (11 tests against the doc) and the
  renderer steps it each frame at the Self-Ear (distance 0, occlusion 0, level) with the speaker
  terms from the local player: `PlayerFaller.isInDanger` (offset 0x44, confirmed from the getter
  body) gates the fall send at the synced `PlayerNetworking.outdoorness` (0x14C); by the game's own
  arithmetic `Dry` = 0 dB, `High` = 0 dB and the boost send is silent at the Self-Ear, so the fall
  reverb is the only live term. Core `MixerStage` (dry gain, Freeverb-style `Reverb` behind the
  sends with `Fidelity.ReverbDecaySeconds`, a 3 kHz `Biquad` high shelf for `High`; 9 + 4 + 4
  tests) runs on the encoder thread after the voice EQ; toggles `Fidelity.MixerDry/High/
  ReverbFall/ReverbBoost` under the `MixerStage` master; unreadable inputs bypass the stage and log
  once; the seven input symbols are in `GameSymbols.Bind`. `Mixer stage: falling` log line on each
  fall (first 10) and a `mixer stage:` segment in the stats line. `REQ-MIXER-RESYNTH` doc + impl +
  unit. 170 tests. Operator check owed: A/B `Fidelity.MixerReverbFall` by jumping off something
  outdoors while talking (the Sink should carry a reverb tail while falling that decays within a
  second or two of landing; the log shows `Mixer stage: falling`); with everything else at the
  Self-Ear being unity, `MixerStage` on/off should be inaudible when not falling. Reverb character
  stays approximate until a second-client recording exists (question 4).
- **T2 built** (2026-09-08): question 3's recipe implemented on the main thread
  (`WorldManager.localPlayerCharacter.hands.heldProp.radioVoiceAssigner`, `_cachedVoiceType ==
  Megaphone`, `isBroadcasting && latestBroadcastPlayer == me` by pointer; symbols in
  `GameSymbols.Bind`; "picked up / put down" and "broadcast started / ended" log lines, first 20).
  Rendering: a listener beside the holder hears the direct voice plus the prop's Megaphone
  `VoicePlayer` output, which reads the same dynamics-processed ring, so the mod renders Core
  `MegaphoneVoice` from the processed frame and adds it (`Fidelity.MegaphoneMix` = Add default /
  Replace). Chain = `VoicePlayer.Update`'s remote-holder branch at d = 0: Core `BitCrusher` (ported
  from the managed Burst fallback `Process$BurstManaged`: per hold of `sampleRate / crushRate`
  samples, quantize the first sample, ramp toward the next hold's by `smooth`, mix `dryWet`;
  `ampVal = 2^(bits-1)`, `crushScale = log(bits+1)/log(25)/ampVal`, constants 1.0 / 25.0 / 0.5 /
  -1.0 read from GameAssembly.dll `.rdata`; at the game's 24 bits the quantization is inaudible
  and the 4000 Hz hold is the effect), 300 Hz Q 0.4 high-pass (`Biquad`), then the mixer's
  compressor (-25 dB, +6 dB) and post-compressor (-15 dB, release 0.25 clamped to FMOD's 10 ms
  floor) as Core `Compressor` (FMOD-style 2.5:1 hard knee, attack-timed peak detector;
  approximate); reverb send off, HP/LP open, channel wet 0 dB / dry -80 dB at that distance, so
  nothing else applies. Toggles `Fidelity.MegaphoneCrusher/HighPass/Compressors` under
  `MegaphoneVoice`. 8 + 4 + 5 tests; `REQ-RENDER-MEGAPHONE` doc + impl + unit. 187 tests.
  Operator check owed: pick up a megaphone in a solo session, use it while talking, drop it: the
  Sink carries the crushed, thinned, squashed megaphone voice on top of the direct voice while
  broadcasting and only the direct voice otherwise; the log shows `Megaphone: picked up`,
  `broadcast started (#1)`, `broadcast ended`, `put down`; the stats line's `megaphone:` segment
  counts broadcasts and frames. Open: whether the megaphone prefab's `roomName` token also opens
  a peer-facing channel the transmit gate should count (it should: token rooms are part of signal
  (a) since M2), to be confirmed from the `Transmit signal` lines during the run.
- **M3 run 2** (2026-09-08 04:15-04:17 local, operator solo run on `1d10acd`, default feed): the
  operator did not fall or use a megaphone, so neither T1 nor T2 was exercised: `falls 0`,
  `megaphone: none, broadcasts 0`, no `Mixer stage: falling` or `Megaphone:` lines, `Transmit signal`
  rooms `[Echo]` only (the token-room question stays open). Counters clean: 974 frames encoded =
  decoded = sent, `dropped 0`, `underruns 0`, `errors 0`; Helper `underruns 0, starves 0, trims 0`,
  1040 frames. `Offset:` 198-222 ms (per-frame 147-261 ms), as in run 1. Mixer stage `live`, dry
  0.0 dB, high -0.0 dB, fall/boost -80 dB all run, so `MixerStage.Process` was the identity (a -80 dB
  send maps to gain 0, the shelf is skipped below 0.01 dB) and the megaphone chain never ran.
  Operator's ear: Local Voice audible, no reverb (none was triggered), and a **new medium-low
  buzz accompanying speech** compared with run 1. The T1/T2 code cannot be the source in this run
  (identity, above); the one figure that differs from run 1 is level: `pre-clip peak 1.15`
  (soft clip engaged; run 1 stayed at 0.44) and Sink `peak 0.925` (run 1: 0.23-0.44), so the lead
  candidate is the game's own soft clip on louder speech. Open: A/B with `Fidelity.MixerStage`
  and `Fidelity.MegaphoneVoice` off, an OBS recording of the buzz for spectrum analysis, and the
  run 2 checks themselves (fall outdoors while talking; hold and use a megaphone).
- **T3 built** (2026-09-08): `OffsetRow` (main thread, ticked from `TravelEarBehaviour`) scans for
  `SettingsMenu` instances every 2 s (`Resources.FindObjectsOfTypeAll`, prefabs skipped; the main
  menu and the pause menu each own one) and clones the Audio category's last `SettingsRow` after
  itself, disables the clone's `SettingsRow` in the same frame (its Start would look up a hanger by
  `settingsType`), deactivates every `Selectable` (no navigation target; the category wired its
  navigation over the original rows in `SettingsCatagory.Start`) and every text but the title, and
  writes the caption into the title as a raw `LocalizedText` value. Caption `TravelEar offset: N ms`
  (`measuring` before the first average) from `OffsetMonitor.LastAverageMs`, refreshed every 10 s
  (Core `OffsetRowCaption`, 4 tests). Question 5's fallback is built in: the settings-UI types are
  resolved softly (not in `GameSymbols.Bind`), and the first exception anywhere drops the row with
  one `Offset row: unavailable` warning while the `Offset:` log line stays. ModSettingsMenu check
  (static, from the decompiled 1.1.2 DLL and its README): it enumerates every loaded plugin's
  `Config` with no registration, bool as on/off, enum as a selector, float and string as text
  fields, titled by key; per-entry descriptions are NOT displayed (only a mod-level description
  passed through its optional registration), so the "with its description" clause of this task
  does not hold for that mod and the docs say "titled by key". `REQ-CONFIG-BEPINEX` doc + impl;
  `REQ-OFFSET-MEASURE` doc line for the row. 191 tests. Operator check owed: open Settings > Audio
  in the main menu and in the pause menu; the last row reads `TravelEar offset: measuring` in the
  main menu and `TravelEar offset: N ms` in the pause menu once the Helper has streamed; the row
  cannot be selected with a controller; the log shows `Offset row: added to the Audio settings`
  twice (main menu, pause menu) and no `Offset row: unavailable` warning.

- **T2b built** (2026-09-08): background agent's bodies read (`docs/reference/
  big-walk-environment-reverb.md`: `AudioDynamicReverb.UpdateReverb` writes fourteen SFX Reverb
  floats on the main mixer every LateUpdate from `RoomSize / Outdoorness / ReverbTime /
  Diffusion`, e.g. `Room = -150 - 400 O - 200 RS mB`, `DecayTime = clamp(15.9 RT^4 + 0.1, 0.1,
  16)`, `DryLevel = -150 - 250 RS`; the mixer assets read with UnityPy give the voice's route:
  voice mixer `Dry` -3 dB -> main `Voice` -> `VOICE DRY BUS` -6 dB to `Master Dry` and `VOICE WET
  BUS` 0 dB into the reverb, whose `DryLevel` adds a second dry copy; the per-voice sends are
  pre-fader, `ReverbFallWet` feeds a fixed 4 s return and `ReverbBoostWet` a fully-wet copy of the
  same environment reverb; `High{n}` is a 250 Hz band level, not a 3 kHz shelf, irrelevant at
  0 dB). One erratum fixed in the doc: outdoors `Reflections` is -8370 mB, the clamp bites only
  at RS >= 0.94. Built: Core `EnvironmentReverbParams` (fourteen floats + `MasterWet` + voice
  slider, `FromListener` = the formulas, Unity's clamps, mB gains), `SfxReverb` (pre-delay line,
  six-tap early FIR at `ReflectDelay`, eight combs / four allpasses `ReverbDelay` later; RT60 from
  `DecayTime`, in-loop damping from `DecayHFRatio` (ratio > 1 capped), comb spread from `Density`,
  allpass coefficient from `Diffusion`, RBJ high/low shelves for `RoomHF`/`RoomLF` at the two
  references; `Biquad` gained `LowShelf`), `EnvironmentReverb` stage (`x = -3 dB voice; out =
  -6 dB x + MasterWet (10^(DryLevel/2000) x + reverb(x))`, clamped). Renderer reads the live
  floats each frame from `AudioDynamicReverb` (or `AudioBasicReverb` in Basic mode, logged once),
  `Bypass` -> dry 0 dB / no room, `Mixer.GetFloat("MasterWet")`, `VoiceNormalVol *
  VoiceAudioSettingsVol`; applies the stage last, after the megaphone mix; resets its tail with
  the mixer stage's on a 2 s gap; `Environment reverb:` line every 10 s with the four scalars and
  all fourteen floats; stats line gains `environment reverb: live/bypassed, room, decay, dry,
  return, frames`. Symbols: `AudioManager.AudioBasicReverb`, `AudioDynamicReverb.Bypass /
  RoomSize / ReverbTime / Diffusion / DSP_*` (14), `AudioBasicReverb.Bypass` + the 14 bare names,
  `GlobalAudioEffects.Mixer / VoiceNormalVol / VoiceAudioSettingsVol` (all confirmed in the
  interop metadata before binding). Config `Fidelity.EnvironmentReverb` (on), `EnvironmentReverbDryCopy`
  (on), `EnvironmentReverbBusGains` (on), `EnvironmentReverbVoiceSlider` (off). Not done: the
  doc's proposal to re-base T1's fall/boost sends on `SfxReverb` (fixed 4 s fall return; the
  boost as a fully-wet copy of the environment set) stays a T5 seed; the master Duck limiter
  (-3 dB, 10:1) is not ported. 19 new tests (formulas vs the doc's corridor, clamps, onset
  delays, room scaling, RT60, HF ratio, both shelves, bypass, bus arithmetic, toggles, clamp).
  Operator check owed: hallway A/B.
- **T4 prep** (2026-09-08, while run 3 waits on the operator): the release runbook's artifact
  layout was stale (no `TravelEar.Core.dll`; the Helper placed under `plugins`, which BepInEx
  would scan). `scripts/release.ps1` now assembles `dist/TravelEar-vX.Y.Z/` as a `BepInEx\` tree
  (`plugins/TravelEar/{TravelEar,TravelEar.Core}.dll`, `TravelEar.Helper/TravelEar.Helper.exe`
  single-file self-contained publish), zips it, writes `SHA256SUMS.txt`, extracts the changelog
  section to `RELEASE-NOTES-vX.Y.Z.md`, and refuses a version mismatch between
  `Directory.Build.props` and `Plugin.cs` or a missing `## [X.Y.Z]` section (`-Draft` takes
  `[Unreleased]` for a smoke test). Runbook steps 5-7 rewritten around it (fresh-install check is
  step 7). `CHANGELOG.md` `[Unreleased]` rewritten as the v1.0.0 body per the runbook's rules
  (user-facing only, no milestone codes; Local Voice, environment reverb, fall reverb, megaphone,
  transmit gate, Helper + OBS, Offset row, settings, test tone, requirements). README: stale
  "implementation not started" status replaced, install steps match the zip layout, OBS /
  `SinkEndpoint` / Offset sections, settings table trimmed to the common keys with a link to the
  full docs-site table. docs-site install page: zip layout, ~200 ms figure, Offset row + the real
  `Offset:` log line. Not done until run 3 and the operator's word: version bump 0.1.0 -> 1.0.0,
  the `## [1.0.0]` retitle, tag, `gh release create`, fresh-install run, and question 4's default
  (T2b made the environment reverb live-driven, so `EnvironmentReverb` on is the proposed default;
  `MixerReverbFall` stays on with its approximate decay unless the operator objects).
- **M3 run 3** (2026-09-09 00:36-00:43 local, operator solo run on `30b053e`, default feed):
  operator's ear: no processing effects, no reverb in the hallway, no difference outdoors;
  Settings > Audio had nothing new. Log: two defects, each logged once at load. (1)
  `Environment reverb: inputs unreadable, stage bypassed: Method not found: '!0 ByRef
  Il2CppSystem.ReadOnlySpan`1.GetPinnableReference()'` — `AudioMixer.GetFloat("MasterWet")` is an
  unstripped Unity 6 body whose span pin is stripped; the `GetFloat_Injected` icall is absent from
  GameAssembly.dll, so there is no read path (reference doc section 6). The stage was bypassed all
  run (`environment reverb: bypassed (inputs unreadable)`), which is why the hallway carried no
  reverb. Fix: `MixerFloats`, a Harmony postfix on `AudioMixer.SetFloat(string, float)` (symbol
  `AudioMixerSetFloat` in `GameSymbols.Bind`) remembering the game's `MasterWet` writes; the
  renderer assumes 0 dB and says so once until the first write, then reports the value and the
  write count in the `Environment reverb:` line and the stats line. (2) `Offset row: unavailable
  (InvalidOperationException: The cloned SettingsRow has no title.)` — the Audio category's last
  row has no `title`. Fix: the template is the last row carrying a caption (`title`, then
  `sliderLabel`, then `arrayLabel`) and the clone keeps that field; the success line names the
  field. Also unexercised again: falls 0, megaphone broadcasts 0 (T1/T2 checks still owed).
  Counters clean: 6021 frames encoded = decoded = sent, `dropped 0`, `errors 0`, mixer stage
  `live` (identity at the Self-Ear); Helper `underruns 0, starves 0, trims 0`, 6021 frames.
  Gates green, 210 tests, deployed 01:17 local. Operator check owed (run 4): hallway A/B, the
  `Environment reverb:` line showing `MasterWet … (N writes)` or `(assumed)`, stats
  `environment reverb: live`; Settings > Audio last row `TravelEar offset:` in both menus and
  `Offset row: added … via <field>` twice; plus the T1/T2 checks (fall outdoors while talking;
  hold and use a megaphone).
- **M3 run 4** (2026-09-09 00:36-02:45 local, operator solo run on `b232692`, default feed; all
  checks exercised: hallway, big indoor room, outdoors, 7 falls, a megaphone, both menus).
  Log: `MasterWet` written by the game (0 dB, 155k writes by the end: it is written every frame
  along with the DSP floats), environment reverb `live` all run, `Offset row: added` twice (via
  `sliderLabel`), 7 `Mixer stage: falling` lines, no warnings; counters clean (54039 frames,
  `dropped 0`, `errors 0`; Helper `underruns 0, starves 0, trims 0`). The game's parameters
  differ strongly between the hallway (RS 0.3-0.5, O 0.07-0.14, Decay 0.9-1.1 s, Reverb -620..
  -870 mB) and the big room (RS 0.42, O 0.65, Decay 0.24 s, Reverb -2240 mB). Operator's ear:
  (1) reverb present indoors but the big room sounded much like the hallway, whereas peers hear
  the hallway distinctly stronger; (2) the reverb cut off abruptly once quiet; (3) Local Voice a
  little hot; (4) the gate over-eager on quiet word ends even with the game's noise suppression
  off; (5) the settings row wrapped into a column (caption landed in the slider's value box);
  (6) the two red bells' effect (voices distort low and slow down nearby, go silent up close)
  not applied to Local Voice. Also: `megaphone: none, broadcasts 0` all run although the
  transmit signal showed the `MegaphoneA` token room transmitting: the held-prop chain
  (`hands.heldProp.radioVoiceAssigner`) read nothing, silently. Diagnosis for (1)+(2): the
  stages ran only on passed frames (`environment reverb: frames 23923` = passed 23923), so every
  tail was cut when the gate closed ~360 ms after speech; a 1 s hallway tail truncated to 0.36 s
  is the big room's 0.24 s tail. (3) is the game's arithmetic: -6 dB dry bus + the reverb's own
  dry copy (`DryLevel` -250..-400 mB) sums to about +1 dB (`dry 1.13 (copy 0.63)` in the stats
  line). Fixes (this commit): reverb tails rendered through the gate's silence for up to 4 s
  after the last passed frame (`RenderTail`; stats `tail frames`); `Fidelity.TransmitHoldMs`
  (0 = automatic) and `Fidelity.OutputTrimDb` (0) knobs; megaphone fallback: a transmitting
  `Megaphone*` room counts as broadcasting (stats `via prop|room`), with a `Megaphone: probe`
  line (every 5 s, 10 max) dumping the held-prop chain while the room is open so the proper
  path can be fixed; Offset row caption = the row's widest heading `LocalizedText` (not the
  value label), word wrap off, log says `via heading '<name>'`; `MixerFloats` logs the first
  write of every distinct exposed float (60 max) and the stats line counts them, so the next
  run maps what the game drives near a red bell. `SfxReverbSceneTests` (2): with run 4's real
  parameters the hallway's tail after 0.4 s must sit >= 10 dB above the big room's and still
  ring a second in. Red bells + water + the rest of the effect map: a background analysis is
  writing `docs/reference/big-walk-voice-effects-catalog.md` (every scenario, mechanism,
  parameters, what the mod covers); its gaps become M4 seeds. Operator check owed (run 5):
  hallway vs big room contrast and the tail after each phrase; whether the megaphone now renders
  (`Megaphone: broadcast started`, `via room`, and what the probe lines say); the settings row
  on one line in both menus; try `TransmitHoldMs` 400-500 and `OutputTrimDb` -3 if the gate or
  level still bother; walk up to a red bell while talking and note the `Mixer floats: first
  write` lines.
- **M3 run 5** (2026-09-09 ~03:00-03:20 local, two launches on `d57f994`; the second, a short
  settings check, overwrote the first's `LogOutput.log`, so only the short one is on disk).
  Operator's ear: the hallway now sounds different from the big room and the reverb no longer
  cuts off; words still cut a little and Local Voice is still a bit hot (better); **A/B against
  remote players in the big room: Local Voice's echo and reverb are far too wet**; no settings
  row visible. Operator rulings: (a) the fall reverb makes no sense on Local Voice (the Self-Ear
  never recedes from the speaker) -> `Fidelity.MixerReverbFall` default off, kept for
  comparison; (b) the wet levels must come from measurement, not trial and error. Short-run log:
  `Offset row: added … from row 'SettingsRow_Button Reset' via heading 'Title' (800 px wide)` —
  with run 4's field filter gone the template became the category's last row, the Reset button,
  whose clone is not visible in the list -> the template is now the last row that is a setting
  (slider, title or array label). `Mixer floats: first write` listed 35 exposed floats the game
  writes in the menu alone: `MasterLP 22000`, `MasterFreqGain3k/1k 1`, `VoicePitch 1`,
  `FoleyPitch`, `PropPitch`, `SuperWetPitch 1`, `BiomeAmbPitch`, `SuperWet_Blindfold /
  _Speechlessness / _Ending -80`, `Voice_Dry 0`, `Voice_Wet 0`, `Voice_SuperWet 0`, `MasterWet 0`,
  the ambience volumes / HP / LP, and the 14 SFX Reverb floats (`DryLevel … Density`) —
  `VoicePitch` and `Voice_SuperWet` + `SuperWetPitch` are the first candidates for the red
  bells. Fixes (this commit): `SessionLog` copies every TravelEar line and every warning/error to
  `%LOCALAPPDATA%\TravelEar\logs\game-<timestamp>.log` per launch (newest 12 kept), so no run is
  lost again; Offset row template rule; fall reverb default off; the operator's config set to
  `TransmitHoldMs = 450`, `OutputTrimDb = -3` to try. Megaphone and red-bell evidence from the
  first launch is gone; owed again in run 6.

### T4a — Reference capture (added 2026-09-09 after run 5)

Why: the operator's A/B says Local Voice's reverb is far wetter than a remote voice in the same
room, and question 4 always said the reverb is calibrated against a second-client recording.
Guessing FMOD's SFX Reverb calibration (what 0 mB of `Room`/`Reverb` means in absolute wet
energy) is the one thing the decompile cannot answer; a measurement can. Design:

- Two machines: the operator's PC as the **listener** (mod installed, `Calibration.Capture =
  true`), a second machine as the **speaker** joining the session, feeding a known test signal
  into its mic (VoiceMeeter can play a WAV: a click train + a 2 s log sweep + a speech clip,
  repeated), standing at the Self-Ear distance in each place of interest (hallway, big room,
  outdoors, falling, with a megaphone, near a red bell).
- The listener's mod records, time-stamped, into `%LOCALAPPDATA%\TravelEar\calibration\<run>\`:
  (a) each remote voice's decoded PCM as it leaves its `AudioFilterMixer` (the `TapFilter`
  postfix already fires for every mixer; for non-local mixers it writes `remote-<n>.wav` when
  capture is on) — the exact input to the game's mixer path; (b) every mixer float write with
  its time (`MixerFloats` -> `floats.csv`: `Dry{n}`, `High{n}`, the sends, the 14 DSP floats,
  `MasterWet`, `VoicePitch`, …) — the exact parameter timeline; (c) the game's final output: the
  Helper gains `--loopback <device>` (NAudio `WasapiLoopbackCapture`) and the mod starts one
  when capture is on, writing `output.wav`; (d) `Local Voice` itself for the same signal is not
  needed: the Core chain is re-run offline on (a) with (b).
- Offline: `tools/calibrate.py` (numpy) aligns (a) and (c) by cross-correlating the click train,
  measures per place the dry gain, the early/late wet-to-dry energy ratio and the decay, then
  renders (a) through the Core chain (`TravelEar.Calibrate` console tool referencing Core, given
  the params from (b)) and reports the delta per stage. Constants that the measurement fixes
  (SfxReverb's wet calibration, the bus sum, the megaphone compressors) go into Core with the
  measured figures cited in the reference doc; `SfxReverbSceneTests` gains the measured targets.
- Also answers: the fall reverb ruling (does a peer beside a faller hear it at all), what the
  megaphone chain really sounds like beside the holder, the red-bell mechanism, and question 4.
- Tag: `[doc->REQ-MIXER-RESYNTH]`; impl under the existing requirement.
- **Topology (operator, 2026-09-09):** the second machine is a Switch 2 with no way to feed its
  mic, but its audio OUTPUT can be captured on this PC. So the roles flip: this PC is the
  SPEAKER (the mod on it, the test signal into its mic through VoiceMeeter's recorder), the
  Switch is a real LISTENER standing beside the PC's avatar, and the Switch's output captured on
  the PC is literally what another player hears, whole console mix included. Local Voice for the
  same signal is written at the same time, so peer vs Local Voice is the delta directly. Absolute
  level is unknowable through a console volume and a capture card, so all comparisons are
  relative to a reference segment (outdoors) and everything else is ratios: dry level, wet-to-dry,
  decay, spectral tilt. Ambience on the Switch is a confound; the click train and sweeps separate it.
- **Built** (2026-09-09): `Calibration.Capture` / `Calibration.CaptureDevice` config
  (`CalibrationCapture`: `input.wav` = decoded outbound voice before the chain, `local-voice.wav`
  = the chain's output sample-aligned, `frames.csv` = per-frame gate disposition + encode stamp +
  wall clock, `floats.csv` = every mixer float write via `MixerFloats.Observer`, `session.txt` =
  the config in force; the stats line shows the capture's progress); Core `WavWriter`; Helper
  `--capture <device> --out <file>` (`CaptureRecorder`, NAudio `WasapiCapture` of a capture
  endpoint to WAV, window = stop), started by the mod as a second Helper when `CaptureDevice` is
  set (`HelperLauncher.TryLaunchCapture`); `tools/make-calibration-signal.ps1` (Windows TTS
  sentence + clicks + 2 s log sweep + silences per cycle, 48 kHz 16-bit; the wav is git-ignored);
  `tools/calibrate.py` (numpy/scipy/soundfile: resample + downmix peer, envelope
  cross-correlation alignment, phrases from `frames.csv`, per phrase peer-vs-local level, tail
  energy in three windows after the phrase, 1/3-octave tilt; `--reference` normalises the peer
  level on a named segment). Gates green, 212 tests. Operator procedure: set `Capture = true` and
  `CaptureDevice` to part of the capture card's name; generate the signal once
  (`pwsh tools/make-calibration-signal.ps1`), play it on loop from VoiceMeeter's recorder into
  the game mic; the Switch listens beside the PC's avatar; visit outdoors (reference), the
  hallway, the big room, the megaphone, a fall, a red bell, noting the wall-clock time of each;
  then `python tools/calibrate.py <capture dir> --segments seg.csv --reference outdoors`.

### T4c — Fact-based gaps from the effects catalogue (added 2026-09-09)

`docs/reference/big-walk-voice-effects-catalog.md` (background analysis, committed `dd4081c`)
catalogued every effect a remote voice meets: 32 scenarios, every exposed float with defaults and
writers, no snapshots anywhere, the full listener path with gains, 12 errata. What needs no
measurement is implemented here; the rest are seeds.

- **Built** (2026-09-09): (0) indoor attenuation, the catalogue's top gap and the likely "hot"
  indoors: `AudioSourceController`'s `_outdoornessVol = listenerOutdoorness * 0.5 + 0.5`
  (-6 dB fully indoors, smoothed 3/s) — Core `SourceVolume`, applied after the in-process
  filters and before the mixer stages, `Fidelity.IndoorAttenuation`; (1a) the red bells' volume
  term `_speechlessVol = 1 - speechlessness` (lerp 5/s), same class, symbols
  `PlayerCharacter.speechless` + `PlayerSpeechless.speechlessness`, `Fidelity.SpeechlessVolume`;
  (3) the master limiter: `Compressor` gained a ratio and a quadratic soft knee, `MasterLimiter`
  = Duck Volume -3 dB (or the live `MasterLimiterThreshold` float) 10:1, 0.1 ms attack, 125 ms
  release, 20 dB knee, applied last on both the pass and the tail paths, `Fidelity.MasterLimiter`;
  (4) the megaphone mixer: compressor 10 ms / 1000 ms (was 50 / 50), the post stage is the Duck
  Volume (-15 dB, 5:1, 250 ms — the old 0.25 was a units bug — knee 10), plus the fixed 5 kHz
  low-pass, ParamEQ 2500 Hz octave 0.8 gain 2.5 (Q 1.78, +8 dB), the SFX reverb at d = 0 (full
  dry, -10 dB room, 2 s) through `SfxReverb`, and the wet-only 100 ms echo (decay 0.3),
  `Fidelity.MegaphoneMixer`, `MegaphoneToggles.Mixer`; erratum 1: `MixerStageInputs.GlobalVoiceVolume`
  renamed `ListenerReverbTime` and fed from `AudioDynamicReverb.ReverbTime` (boost is still
  silent at the Self-Ear). Stats line: `source gain`, `limiter`. Tests: `ListenerGainTests` (8).
- **Seeds (M4, from the catalogue's ranked gaps):** red bells rows 9-10 — `VoicePitch = 1 -
  sp * SpeechlessPitchDeduction` through an FFT-1024 pitch shifter (phase vocoder port) and the
  super-wet bloom (`SuperWet_Speechlessness = (1 - sp^0.4) * -80` into the fixed 6.8 s reverb +
  chorus, pitched by `SuperWetPitch`), all values already visible in `MixerFloats`; cliff echo
  (`EchoRemote`, two copies 0.7-1.5 s, LP/HP, ducker, 6 s reverb); blindfold worn by the local
  player (full-wet LowPass 1500 Hz Q 0.6 on our voice as others hear it; state from
  `postProcessingManager.blindfoldPPVolume.weight`); blindfold/headphone listening tone
  (`MasterLP`, `MasterFreqGain*`); ending and black tower super-wet returns; walkie/radio (out of
  scope by design); `High{n}` as a 250 Hz band split instead of a 3 kHz shelf; the `Reverb Fall`
  return's real 4 s / HF ratio 2 / +3 dB character (now off by default anyway).

### T0 bodies read (2026-09-07, background agent; full report `docs/reference/big-walk-local-voice-wiring.md`)

The decomp trees from 2026-09-06 were current (GameAssembly.dll 2026-08-28), so no regeneration;
`GlobalAudioEffects`, `AudioSourceController`, `AudioDynamicReverb`, `AudioPool`,
`AudioPlayHelper` and `BiquadFilters` were resolved in addition to the already-resolved
`SelfEcho`, `LocalVoicePlayer`, `VoicePlayer`, `PlayerVoicePlaybackControl`, `PlayerLips`,
`RadioVoiceAssigner`.

**Question 1 (feed point): moved.** A `Clean` `VoicePlayer` in synthesizer mode adds, between
the provider ring read and the source output, only `ring[readHead++]` and
`AudioFilterMixer`'s `clamp(envelope x data, -1, 1)`, where the envelope is the source's gain
chain (Unity spatial gain at the Self-Ear's 3 in, cue volumes), near unity. No type-specific
filter for `Clean`, no mixer floats written (those belong to `PlayerVoicePlaybackControl`,
already ported in M2), no `SamplePlaybackComponent` DSP. `SelfVoice` mode is an Allpass biquad
with `_vol = 0`: silent by construction, so it was never a candidate. The one game filter the mod
attached itself, the 400 Hz PeakingEQ, is a documented RBJ kernel and is now Core `PeakingEq`.
Nothing material remained, so ADR-0005 moves the feed to the encoder thread and keeps the
ADR-0003 path as `Fidelity.SinkFeed = VoicePlayer` for A/B until a release ships on the default.
Hazard 1.1 holds by construction on the default path.

**Question 2 (`LocalVoicePlayer` / `SelfEcho`): not a better host; constants recorded.**
`SelfEcho` owns three `LocalVoicePlayer` emitters (centre, +-60 deg at 1 m), an `AudioMixer`,
the `AudioDynamicReverb` and six heading-sector echo buckets. Its input is the raw Unity
microphone clip (`DissonanceComms.Clip`), not `LocalVoiceProvider`; it is enabled through
`PlayerLips.SetOutdoorEcho(true)` -> `DissonanceComms.AddToken("echo")`. Per `LateUpdate` it
writes `CenterDelay/LeftDelay/RightDelay = 700 + 800 * far/(mid+far)` ms,
`Center/Left/RightDecay = 0.4 + 0.2 * mid`, `MasterVol = lerp(clamp01(height/100) * -6 dB, 3/s)`,
and each emitter's `ScriptableVolume = lerp(outdoorness * (1-near) * (mid+far), 2/s)`, with
near/mid/far the smoothed fractions of reverb rays landing under 50 m / under 200 m / beyond.
`LocalVoicePlayer` plays that mic clip on a pooled `AudioSourceController` with the playhead
pinned 512 samples behind `Microphone.GetPosition`; it hosts no Dissonance DSP and needs an
`AudioClip`, so it cannot host mod PCM. v1.1's cliff echo is a Core re-synthesis from these
constants (M4 seed).

**Question 3 (Megaphone state): answered, recipe recorded.** There is no `Megaphone*` room-name
literal in code; the room name is prefab data on `RadioVoiceAssigner.roomName` (default
`"RadioA"`). The megaphone is a `Prop` whose `radioVoiceAssigner` owns a prefab `VoicePlayer`
with `PlayerType == Megaphone`. Pressing it (`Peck`, networked) sets
`latestBroadcastPlayer`, calls `PlayerLips.SetTalkingIntoRadio(true, roomName)` ->
`DissonanceComms.AddToken(roomName)` on the local player, sets `isBroadcasting` and fires the
static `RadioVoiceAssigner.onChange`; release removes the token and clears `isBroadcasting` once
`GetIsSpeakingInto(roomName)` drops. The remote side hard-switches (`SampleProvider` swap, no
fade). Detection for T2, main thread:
`WorldManager.localPlayerCharacter.hands.heldProp.radioVoiceAssigner` with
`voicePlayer.PlayerType == Megaphone` (read before `StartReceiving` may flip it to `SelfVoice`;
`_cachedVoiceType` is the stable copy); talking = `isBroadcasting && latestBroadcastPlayer ==
me`, cross-checked by the comms holding the `roomName` token; edges from `onChange`. The remote
render applies, in process, a `BitCrusher` (24 bit, 4800 Hz, dry/wet 0.6, smooth 0.6, mono) and
a `BiquadFilters` HighPass 300 Hz Q 0.4, then 13 mixer floats on the cue's mixer (`ReverbDry`,
`ReverbWet`, `ReverbDensity`, `ReverbDecayTime`, `ReverbLF`, `ReverbDecayHFRatio`,
`HPFrequency`, `LPFrequency`, `CompressorGain`, `CompressorThreshold`,
`PostCompressorThreshold`, `PostCompressorRelease`, `PostCompressorGain`) and
`Megaphone{n}Wet/Dry` on its parent, all from `VoicePlayer.Update` with the formulas in the
report's section 3. T2 therefore ports the BitCrusher and high-pass to Core and takes the
megaphone mixer set into the Mixer Stage.

## Gate

`pwsh scripts/gates.ps1` green; every task's operator verification recorded here; the v1.0 tag
exists and the GitHub Release carries the zip.
