# M2 — Filter Stage fidelity, gating, and Offset (JIT plan)

> Second milestone. M1 proved the pipeline end to end (`M1-PLAN.md`, "M1 outcome"); M2 makes
> the Clean path sound like the remote-player path, limits Local Voice to what peers actually
> receive, measures Offset, and hardens the Helper lifecycle. Vocabulary: `CONTEXT.md`.
> Gate: `pwsh scripts/gates.ps1` green with the M2 requirements activated.

## Scope

In: transmit gate (the operator's parked "noise floor"); the remote path's Filter Stage effects
applied to our `VoicePlayer` (compressor, soft clip, makeup gain, ARV, EQ, Self-Ear curves);
Offset measurement and lag margin tuning; Helper lifecycle and Sink format (`Downmix`);
hazard unit tests; Mixer Stage re-synthesis only if time remains (else M3).

Out: megaphone, Offset UI row, settings-menu rows, cliff echo, water muffle, release packaging.

## Requirements activated by M2

Each is activated (`required_stages` set) in the commit that starts its task, per
`traceable-reqs.toml`; stages are added as evidence lands.

- `REQ-VOICE-CONTINUOUS` — add `unit` (T0: gate + silence fill logic in Core).
- `REQ-RENDER-CLEAN` — doc + impl (+ unit for any pure DSP moved into Core) (T1).
- `REQ-EAR-SELF` — add curves/outdoorness (T1); doc already tagged.
- `REQ-OFFSET-MEASURE` — doc + impl + unit (T2).
- `REQ-SINK-LIFECYCLE` — doc + impl + unit (T3).
- `REQ-SINK-FORMAT` — doc + impl + unit (T3).
- `REQ-HAZARD-NO-GAMEPLAY-IMPACT`, `REQ-HAZARD-NO-PARTIAL-FIDELITY`, `REQ-HAZARD-NO-PEER-SURFACE`
  — `unit` (T3/T5); `REQ-HAZARD-NO-GAME-AUDIO-LEAK` — add `unit` (T5).
- `REQ-MIXER-RESYNTH` — doc + impl only if T4 starts.

## Open design questions

1. **Which signal is "peers receive"?** Candidates: (a) any open room channel in
   `WorldManager.instance.dissonanceComms.RoomChannels` whose room is not `Echo`; (b) the
   GhostRoom `VoiceBroadcastTrigger.IsTransmitting`; (c) player channels too
   (`PlayerChannels`). M1 T2 run 4 saw VAD fire on the GhostRoom trigger and Self Echo stay
   open all session. Answer by logging (a), (b) and (c) side by side for one run and picking the
   one that matches speech onsets; prefer (a) because it covers radio/megaphone rooms later.
2. **Where does the remote path's processing live and what order?** Study
   `SamplePlaybackComponent` (`_compressor` VoiceCompressor, soft-clip constants, `MakeupGain`,
   `ARV`/`OutputARV`) and `PlayerVoicePlaybackControl.Update` (FilterDistance/Angle/Attenuation/
   SpatialVol curves, `_eqFilter` BiquadFilters, `_outdoornessVol`/`_amplitudeVol`/
   `_speechlessVol`, Dry/High/ReverbFallWet/ReverbBoostWet mixer floats) with the Cpp2IL recipe
   (memory `travelear-project.md`; ~25 s to regenerate). Decide per effect: reuse a game
   component on our controller (preferred, ADR-0003 rule of thumb) or port the DSP into Core
   with a unit test. Mixer floats (`Dry{n}` etc.) belong to the Mixer Stage and stay out of T1.
3. **Self-Ear angle.** Start at angle 0 on the filter-angle curve; expose
   `Ear.SelfEarAngleDegrees` (default 0) so the operator can calibrate the off-axis `High{n}`
   roll-off by ear (DESIGN "Self-Ear geometry"). Keep it a config knob, not a decision, until a
   second-client recording exists.
4. **Offset reference points.** Capture timestamp = `Stopwatch` at `OpusEncoder.Encode`
   postfix (first sample of the frame); render timestamp = Helper's WASAPI position when that
   frame's first sample leaves the ring buffer. The pipe is one-way today (mod -> Helper);
   Offset needs a return path: a second pipe (`TravelEar.Sink.Back`) or a duplex pipe. Prefer
   the second, inbound-only pipe: no change to the existing frame stream.
5. **WMI spawn.** `Win32_Process.Create` via `System.Management` from a BepInEx IL2CPP plugin
   (net6): confirm the assembly loads under the game's runtime; fallback is a tiny launcher
   step in the Helper itself (`--detach`: re-exec self and exit) so the parent is the short-lived
   launcher, not the game.

## Tasks

### T0 — Transmit gate (noise floor)

- Log the three candidate signals (question 1) once per state change; pick one.
- `TransmitGate` (Core, pure): given `(transmitting, frame)` decide push-frame / push-silence;
  unit tests for onset, release tail (keep a short hold, ~100 ms, so a word's last frame is
  not cut), and the never-gap invariant (`REQ-VOICE-CONTINUOUS` unit).
- Renderer: sample the chosen signal on the main thread each tick (a volatile flag the
  encoder thread reads); when not transmitting, push silence instead of the decoded frame so
  the ring stays fresh and the mic noise floor never reaches the Sink. Config
  `Fidelity.TransmitGate` (default on) for A/B.
- Operator verification: noise floor gone between words; word onsets intact.
- Tag: `[impl->REQ-VOICE-CONTINUOUS]` on the gate path; `[unit->REQ-VOICE-CONTINUOUS]`.

### T1 — Remote-path processing on Local Voice

- Regenerate Cpp2IL bodies for `SamplePlaybackComponent`, `VoiceCompressor`, `VoiceMakeupGain`,
  `PlayerVoicePlaybackControl`, `BiquadFilters`; record what each does in this plan ("T1
  bodies read"), the way M1 did for the VoicePlayer.
- Per question 2: attach the game's components to our controller where possible (e.g. a
  `PlayerVoicePlaybackControl`-equivalent driven with Self-Ear inputs), else port to Core
  (`VoiceCompressor` and soft clip are small DSP; port with tests).
- Self-Ear inputs: distance = `SelfEarForwardMeters`, angle = `SelfEarAngleDegrees`, occlusion
  0, `outdoorness`/`echoAmount` read from the local player each tick.
- Operator verification: A/B each effect via `Fidelity.*` toggles; compare against a
  second-client recording when one exists.
- Tag: `[doc->REQ-RENDER-CLEAN]` in DESIGN (renderer section), `[impl->REQ-RENDER-CLEAN]`,
  `[impl->REQ-EAR-SELF]` on the curve evaluation, `[unit->…]` on ported DSP.

### T2 — Offset measurement and lag margin

- `SinkFrame` already carries a capture timestamp; stamp it at `OpusEncoder.Encode` (carry the
  timestamp with the decoded frame through the provider push and into the Tap ring: the Tap
  cannot see frame boundaries, so keep a small timestamp queue keyed by sample count).
- Helper: on each frame's first sample rendered, write `(captureTimestamp, renderTimestamp)`
  back on the return pipe (question 4). Mod: rolling 10 s average, one Info line every 10 s
  (`REQ-OFFSET-MEASURE`); Core `OffsetAverager` with unit tests.
- Lag margin: make `ReadHeadMarginFrames` a config (`Fidelity.ReadHeadMarginFrames`, default
  1.5) and log resync counts; tune down until resyncs appear, then back off.
- Tag: `[doc->REQ-OFFSET-MEASURE]` in DESIGN (Config section), `[impl->…]`, `[unit->…]`.

### T3 — Helper lifecycle and Sink format

- Spawn per question 5; spawn once per game launch; Helper exits when the pipe closes (already
  true); mod re-arms the pipe every 5 s at most (`SinkPump` already re-arms immediately; add
  the cadence) and never respawns (`REQ-SINK-LIFECYCLE`). Core `HelperLifecycle` policy object
  with unit tests (spawn-once, retry cadence, no respawn).
- `Downmix`: Core `Downmixer` (N -> 1, equal-weight average) applied in the pump before framing;
  `REQ-SINK-FORMAT` doc + impl + unit.
- Hazard tests: `REQ-HAZARD-NO-GAMEPLAY-IMPACT` (closed pipe and failed spawn absorb without
  throwing, retry cadence holds) against the Core policy objects.
- Tag as above.

### T4 — Mixer Stage re-synthesis (if time; else M3)

- Per ADR-0002: read `Dry{n}`, `High{n}`, `ReverbFallWet{n}`, `ReverbBoostWet{n}` for our
  channel via `AudioMixer.GetFloat` each tick; Core DSP: dry gain, high shelf, a simple
  reverb (Schroeder/FDN) with two sends; each toggleable under `Fidelity.*`.
- Tag: `[doc->REQ-MIXER-RESYNTH]`, `[impl->…]`, `[unit->…]`.

### T5 — Close-out

- Hazard units still open: `REQ-HAZARD-NO-PARTIAL-FIDELITY` (bind step with one missing
  symbol => disabled + one log line; needs the bind step's resolver to be injectable),
  `REQ-HAZARD-NO-PEER-SURFACE` (patch-set audit: only postfixes plus the one whitelisted
  `LocalVoiceProvider.Start` prefix keyed to our instance), `REQ-HAZARD-NO-GAME-AUDIO-LEAK`
  (`TapDivert` zeroing for every channel count/buffer size).
- Refresh DESIGN, CHANGELOG, this plan's status log; ADR if T1 changes a decision (likely:
  "remote-path processing is reused from game components" or "ported to Core").
- Write `M3-PLAN.md` just-in-time (megaphone, Mixer Stage if deferred, Offset UI row,
  packaging).

## Status log

- (empty)

## Gate

`pwsh scripts/gates.ps1` green: build, `dotnet test`, `traceable-reqs check` with the M2
requirements at their final stages, `mdbook build docs-site`.
