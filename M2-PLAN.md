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
   **Answered (runs 1-2):** none of the three. In a solo session no room but `Echo` ever opens
   and no trigger but Self Echo ever transmits, while the GhostRoom and proximity triggers'
   own VAD flag (`_isVadSpeaking`) flips with speech; the game's mute stops the encoder
   outright. Signal = not `DissonanceComms.IsMuted` and (any non-`Echo` room open, which keeps
   (a) for the Open-mode token rooms, or any voice-activation trigger's VAD speaking).
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

- **T0 built** (2026-09-07): Core `TransmitGate` (Pass/Silence per frame, 100 ms release hold,
  never-gap invariant) + 8 tests; `REQ-VOICE-CONTINUOUS` unit stage activated. Renderer samples
  `TransmitSignal` each tick: signal (a) = any open room channel in
  `WorldManager.instance.dissonanceComms.RoomChannels` other than `Echo`; encoder thread pushes a
  zero frame instead of the decoded frame when the gate says Silence. Config
  `Fidelity.TransmitGate` (default on). One-run probe logs (a), (b) triggers with `IsTransmitting`
  (collected by a `VoiceBroadcastTrigger.Start` postfix) and (c) open player channels side by side
  once per state change (`Transmit signal #n:` lines). Fail-open if the comms are missing or the
  read throws. Runs 1-2 showed (a) never opens in a solo session; settled on the VAD flag plus
  (a) for token rooms, see question 1 and "M2 run 2" below.
- **M2 run 1** (2026-09-07, operator solo run on `7a541c3`): the gate stayed closed for the
  whole session, so Local Voice was silent and checks 1-3 could not be judged. Probe lines:
  `rooms=[Echo] triggersTransmitting=[Echo(Open)] playerChannels=[]` from world load to exit,
  3 signal changes all run, 7280 frames encoded and every one silenced, no `Offset:` average
  (silence pushes carry no stamp, so no report; by design). No "read failed", no warnings, no
  errors. The GhostRoom (voice-activation) channel that M1 run 4 saw open with speech never
  opened, so by the run-4 finding peers would have received nothing either: the gate did what
  it says, on a session where the game was not transmitting. Why GhostRoom never opened is the
  open point: suspect the in-game voice toggle (M1 run 4 asked the operator to press it; its
  state may persist), else a VAD or trigger condition the probe could not see. Probe widened
  for run 2: every tracked trigger with `*` transmitting / `M` muted / `V` VAD-speaking, plus
  `DissonanceComms.IsMuted` and the local player's `IsSpeaking`. Check 4 (Helper outside the
  game's process tree) **passed**; OBS captured no audio from the Helper window, expected with
  Local Voice silent, to be re-judged in run 2. Operator's separate finding: the "noise floor"
  is analog line noise that tracks the game rendering and stays audible with the game's audio
  muted at the mixer, so it is not Local Voice; the transmit gate remains a fidelity item
  (peers do not hear the Self Echo room), not a noise fix.
- **M2 run 2** (2026-09-07, operator solo run on `c1837d5`, voice unmuted / muted / unmuted):
  widened probe answers question 1. Sixteen triggers tracked: fourteen Open-mode token rooms
  (radio, megaphones, interview, seance, train intercom; none transmitting), Self Echo
  (Open, transmitting all session, the only open room), `GhostRoom` (VoiceActivation) and an
  unnamed VoiceActivation trigger (the proximity one). Both voice-activation triggers' VAD
  flag flipped with speech (80 state changes over the run) but neither ever transmitted, so
  no peer-facing channel opened and signal (a) could not. Mute set `DissonanceComms.IsMuted`
  and froze the encoded-frame count for the muted stretch (encoder otherwise continuous:
  167 x 60 ms frames per 10 s); Dissonance's local `IsSpeaking` stayed true throughout (Echo
  open) and is useless. Gate closed all run again, so checks 1-3 stay owed. **Probe settled:**
  signal = not muted and (non-`Echo` room open or voice-activation VAD speaking); probe text
  and the player-channel, local-state and per-trigger-mute reads removed; the `Start`
  postfix stays (it is how triggers are found). Run 3 owes: noise floor gone between words,
  onsets intact (the VAD onset is what peers get), T1 level and `remote path:` movement, T2
  `Offset:` lines, T3 OBS capture of the Helper window carrying Local Voice.
- **M2 run 3** (2026-09-07, operator solo run on `9750944`): the gate opened with speech
  (`vad True -> gate OPEN`), 1667 frames passed / 1326 silenced. Operator: noise floor gone
  between words, onsets intact, Local Voice level matches a peer, OBS captures the Helper
  window with voice. `remote path:` makeup moved with speech (1.0 -> 1.7 -> 3.3, +10 dB held
  through pauses as the game's loop does). `Offset:` rolling averages 287-305 ms (per frame
  210-380 ms) at margin 1.5 frames, one early window averaging 1191 ms (max 2722 ms) while
  the first resyncs settled; resyncs 16, all "reader overran" before the first burst, none
  after; no drops, no underruns. **Checks 1-4 cleared. New defect:** Local Voice choppy with
  "debris", like a DAW input starved of CPU. Cause: the VAD flag flips several times per
  phrase (231 signal changes, about seven passed frames per flip), the gate was a hard cut
  with a 100 ms hold, so every flip past the hold spliced a zero frame into speech and each
  re-open restarted the makeup loop's first-second slew. What a peer hears instead:
  Dissonance fades the channel in and out (`VoiceBroadcastTrigger._activationFader` driven
  by `VolumeFaderSettings._fadeInTicks`/`_fadeOutTicks`) and the channel stays open through
  the fade-out; in solo that fader never runs because the channel never opens, so the mod
  has to apply the same fade itself.
- **T0 fix: channel fade** (2026-09-07): Core `TransmitFader` (linear per-sample ramp toward
  1 while transmitting and toward 0 otherwise at the fade-in / fade-out rate, zero fade =
  hard, a re-onset mid-fade ramps from the current gain, reports an all-silent frame) + 6
  tests tagged `REQ-RENDER-CLEAN`; `TransmitGate.SetReleaseHold` (+1 test) so the hold grows
  to fade-out + one frame without resetting the counters. Renderer applies the fader on the
  encoder thread before the remote-path dynamics (assumption: the channel volume is a
  property of the stream a peer receives, ahead of their playback processing; revisit if the
  fade sounds compressed), and reads the fade once from the game's voice-activation triggers
  (`TransmitSignal.TryReadFade`, longest fade-out among them; logged as `Transmit fade:`; the
  fader is linear like Dissonance's `Fader.CalculateVolume`: start + (end - start) x clamp(elapsed / duration), read from the ISIL dump). Config
  `Fidelity.TransmitFadeOutMs` (0 = the game's) overrides the fade-out if the VAD still
  flickers audibly; the stats line shows `fade in/out` and `hold`. Operator check owed
  (run 4): speech smooth, no debris, noise floor still gone, onsets intact; the
  `Transmit fade:` line shows the game's values.
- **M2 run 4** (2026-09-07, operator solo run on `b8f704d`): `Transmit fade: in 0 ms, out
  150 ms (the game's '(unnamed)' trigger fades out over 150 ms); gate hold 210 ms`, signal
  changes down to 84 (231 in run 3), silenced 137 of 1349 frames. Operator: debris "maybe
  marginally better / less frequent, definitely still there". So the gate was at most a
  contributor. Mod-side counters are clean: silence frames flat through every speech window
  (the main thread never splices zeros into a burst), resyncs 6 (four "reader overran" before
  the first word, one "burst start"), Sink ring 0 dropped / 0 underruns, `Offset:` 292-300 ms
  averages with a ~200 ms per-frame spread inside a window. Suspects left, in order: (1) the
  Helper's ring, which kept only 30-80 ms of headroom against a 40 ms WASAPI pull and that
  200 ms jitter, so it must have been padding (gaps) and trimming (cuts) during speech; no
  counter reached the log to prove it (the status window shows them, the log did not); (2)
  the endpoint, which Helper.log names as `VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)`, a
  virtual device whose engine crackles under CPU load; (3) something in the frames themselves
  (T1 dynamics, decoder copy), which the counters cannot see.
- **Fix: Sink jitter budget** (2026-09-07): Helper `RingWaveProvider` band raised to 100-200 ms
  (target/trim) from 30-80 ms, and the Helper logs `Stream: frames, buffered, underruns,
  dropped, trims, offset reports` every 10 s. Run 5 discriminates: Helper.log counters at
  zero with debris gone = (1); counters at zero with debris present and the OBS capture of
  the Helper window clean while the headphones are not = (2), the operator's VoiceMeeter
  engine (fix on that side: a bigger VoiceMeeter buffer, or `Sink.SinkEndpoint` pointed at a
  physical device); counters at zero with debris in the OBS capture too = (3), next stop the
  frame path (a `Fidelity.TransmitGate = false` run and a T1-off comparison). A test tone
  from a second Helper (`TravelEar.Helper.exe --tone`) under game load is the quickest ear
  test for (2).
- **T3 built** (2026-09-07, while T0's run and T1's bodies read were pending): Core
  `HelperLifecycle` (spawn once per session, never respawn even after a failed spawn or an exited
  Helper; pipe re-arm delay so arms are never closer than 5 s) + 8 tests, tagged
  `REQ-SINK-LIFECYCLE` and `REQ-HAZARD-NO-GAMEPLAY-IMPACT` unit. Core `Downmixer.ToMono`
  (equal-weight average, in-place safe) + 7 tests; the pump folds each block under
  `Sink.Downmix` and the header says 1 channel (`REQ-SINK-FORMAT`). Question 5 settled on the
  plan's fallback rather than WMI: the Helper grew `--detach` (re-exec self without the flag,
  exit), the mod launches it with the flag, so the surviving Helper's parent is the dead
  launcher, not the game; no `System.Management` under the game's runtime to prove. The
  launcher now goes through the policy object and logs one line per skip reason. Operator
  check still owed: after a game launch, Task Manager shows the Helper outside the game's
  tree, and OBS's game capture stays free of Local Voice with `SinkEndpoint` on the default
  device. `HelperOptions` gained the flag with a round-trip test.
- **T2 built** (2026-09-07, still ahead of T0's run and T1's report): Offset end to end,
  question 4 settled as planned (second inbound-only pipe `TravelEar.Sink.Back`, 20-byte
  `OffsetReportFrame` records). The "timestamp queue keyed by sample count" became a
  position-keyed table instead: Core `FrameStampTable` (writer marks span + timestamp per
  block, reader resolves a position modulo the ring, newest mark wins, per-slot seqlock so the
  audio thread never locks) + 8 tests including a producer/consumer torn-read test. Why
  positions, not counts: the provider ring is written by two threads (encoder voice, main-thread
  silence) and the read head is jumped by resyncs, so counting samples would drift; matching by
  wrapped ring index survives both, provided every push marks (silence marks "none", which also
  shadows stale marks a ring ago). Three hand-offs: provider ring (`RoundTripProvider.Push` now
  takes the sample count and timestamp and serializes pushes with a lock; the Tap resolves
  `_readHead - block.Length` against it), Sink ring (`TapFilter.SinkStamps`; the pump stamps
  the header, 0 = none), Helper ring (`RingWaveProvider.Stamps`; render time = now + queued
  seconds from `WasapiOut.GetPosition()` against bytes provided, nominal 40 ms if the clock
  throws). Helper drains reports on its own thread to the return pipe (drops them when the mod
  is absent, bounded queue); mod `OffsetMonitor` serves the pipe with the same 5 s re-arm
  cadence, `OffsetAverager` (10 s window, 5 tests) and logs `Offset: N ms rolling 10 s average
  (count, min-max)` every 10 s; `LastAverageMs` kept for the M3 settings row. Config
  `Fidelity.ReadHeadMarginFrames` (default 1.5) replaces the constant; resync count was already
  in the stats line. `REQ-OFFSET-MEASURE` doc+impl+unit. Operator check owed: after a run, the
  `Offset:` lines exist, the number is plausible (expect roughly the 150-180 ms ring lag plus
  ~50 ms of Sink/endpoint), and lowering the margin lowers it until resyncs climb.
- **T5 hazard units built** (2026-09-07, pulled forward while T0/T1 waited): `REQ-HAZARD-NO-GAME-AUDIO-LEAK`
  unit = `TapDivert` sweep over 1/2/4/6/8 channels x 256-4096 frame blocks x empty/half/full
  ring (block always all zeros afterwards, ring holds exactly what it accepted).
  `REQ-HAZARD-NO-PARTIAL-FIDELITY` impl+unit = the bind step's bookkeeping moved to Core
  `SymbolBinder` (injectable lookups; a lookup that returns null or throws is a miss; `Complete`
  yields one verdict and one log line naming every miss), `GameSymbols.Bind` now goes through
  it. `REQ-HAZARD-NO-PEER-SURFACE` unit = source audit of `src/TravelEar/*.cs` (the plugin
  assembly cannot load in a test process): only `[HarmonyPostfix]` plus the one whitelisted
  prefix `RoundTripProvider.SkipMicSubscription`, which must return true for every instance but
  the mod-owned pointer; no transpiler/reverse/finalizer patches; no send/join/network calls in
  code (comments excluded).

- **T1 built** (2026-09-07, after the bodies read): Core `VoiceCompressor`, `VoiceDynamics` (gain
  ramp -> compressor -> soft clip, in place on a mono block; meters arv, output arv, pre-clip
  peak, reduction) and `VoiceMakeupGain` (one instance, the game's constants), unit-tested against
  the reference. Renderer: `ProcessRemotePath` on the encoder thread between decode and push
  (burst start = the game's session reset), `UpdateMakeupGain` once per frame on the main thread
  (speaking = a gate-passed burst within 250 ms; `TargetARV` and `VoiceCompressor.Threshold`
  read from the game, fallback to the reference level), `RefreshEq` attaches a `BiquadFilters`
  PeakingEQ (400 Hz, Q 0.3, +30 dB, Vol 0.03) to the pooled source only when config
  `Fidelity.SelfEarEqDryWet` > 0 (at 0 it is an exact passthrough and a component on a pooled
  source would follow it to its next owner), removed when the controller changes. The stats line
  gained a `remote path:` segment. The decoder copies the IL2CPP buffer through a managed scratch
  (2 x 2880 floats per frame) rather than aliasing the IL2CPP array. Question 3 stays a knob.
  Operator check owed: Local Voice level matches what a peer hears; `remote path:` shows makeup
  moving with speech and settling; no "EQ attach failed" warning when the knob is raised.
  Committed `6fd9ebb` (two meter assertions relaxed to four places: a float32 sum over a
  2880-sample block drifts in the sixth).
- **T5 close-out, docs half** (2026-09-07): ADR-0004 records the T1 decision (port the dynamics
  and makeup gain to Core, reuse only `BiquadFilters`, read the game's statics never write them)
  and adds a clause to ADR-0003's rule of thumb. CHANGELOG has the T1 entry; the docs-site
  settings page lists every config key and says only `Sink.Downmix` applies live (all the
  renderer inputs are constructor arguments). **T4 (Mixer Stage re-synthesis) deferred to M3:**
  every M2 task still owes an operator run, and a reverb built blind on top of unverified
  dynamics would only add to what one run has to disentangle. M2 closes once the operator's
  run clears the four owed checks (T0 noise floor, T1 level, T2 `Offset:` lines, T3 Helper
  outside the game's tree); any failure becomes a fix task here before M3-PLAN is written.

### T1 bodies read (2026-09-07, background agent; full report `docs/reference/big-walk-voice-dsp.md`)

Chain per DSP block on a remote voice: constant-1.0 clip carries Unity's spatial gain, then
`AudioFilterMixer` runs `SamplePlaybackComponent.ProcessSamples` (gain ramp to `MakeupGain` ->
`VoiceCompressor.Process` -> `SoftClip` -> `data[i] *= voice`), then `BiquadFilters _eqFilter`
(PeakingEQ 400 Hz, Q 0.3, +30 dB, Vol 0.03, DryWet from distance/angle curves), then the
blindfold low-pass (bypassed), then clamp +-1; synthesizer mode is not used on that path (our
Clean `VoicePlayer` path does use it). Per frame `PlayerVoicePlaybackControl.Update` writes the
mixer floats for its pooled channel and calls `VoiceMakeupGain.Evaluate(name, arv, isSpeaking,
dt)`. Exact constants: compressor threshold 0.6 (or `min(TargetARV*4, 0.6)`), knee 0.4 x
threshold, attack 5 ms, release 150 ms, ratio 2; soft clip knee 0.85, headroom ~0.1, asymptote
0.95; makeup gain targets ARV 0.132 with 24 dB/s slew in the first second of speech, then 12 up
/ 1 down, gate `max(0.005, env*0.1)`; `arv` = mean |sample| of the block before gain and
compressor.

Decisions (question 2): **port** the compressor, soft clip, gain ramp and ARV metering into Core
(`VoiceDynamics`, pure, unit-tested against the constants above) and apply them on the encoder
thread before the provider push; **port** `VoiceMakeupGain` too (40 lines; reusing the game's
static dictionary would need a distinct key and still risks its shared state), reading the
game's `TargetARV`/`Threshold` statics read-only so the in-game voice slider still couples;
**reuse** `BiquadFilters` by attaching one to our controller's GameObject after our provider,
configured as `PlayVoice` does, with `DryWet` hard-set (default 0 = dry, config
`Fidelity.SelfEarEqDryWet`) instead of replicating curves the Self-Ear evaluates at their left
edge (question 3 stays a knob). **Do not** write `Dry{n}`/`High{n}`/reverb floats: those
belong to a pooled channel another player owns; the Self-Ear is dry, unreverbed, unoccluded by
construction (Mixer Stage remains ADR-0002's own re-synthesis, T4/M3). `isSpeaking` comes from
our burst detection. Audible if skipped: level mismatch against remote voices (makeup gain),
peaks hitting the hard clamp instead of the 0.95 soft ceiling.

## Gate

`pwsh scripts/gates.ps1` green: build, `dotnet test`, `traceable-reqs check` with the M2
requirements at their final stages, `mdbook build docs-site`.
