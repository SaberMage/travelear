# M1 — feasibility spikes and the pure core (JIT plan)

> First milestone. Retire the three risks that decide whether the design in `docs/DESIGN.md`
> holds, and lay down the pure (game-independent) code they need. Vocabulary: `CONTEXT.md`.
> Gate: `pwsh scripts/gates.ps1` green with the M1 requirements activated.

## Scope

In: the three spikes from `docs/DESIGN.md`, a `TravelEar.Core` library for game-independent
code with unit tests, a minimal working Helper, and a plugin bind step that resolves every game
symbol up front.

Out: Mixer Stage re-synthesis, megaphone, Offset UI, settings-menu rows, release packaging.

## Requirements activated by M1

Activated at `doc` now (evidence tagged in `docs/DESIGN.md`); `impl` and `unit` are added to
each as its evidence lands, per the activation model in `traceable-reqs.toml`.

- `REQ-VOICE-OUTBOUND-TAP` — spike S2.
- `REQ-VOICE-ROUNDTRIP` — spike S3.
- `REQ-SINK-HELPER-PROCESS`, `REQ-SINK-ENDPOINT-CONFIG` — spike S1.

## Open design questions (answered by the spikes)

1. Does OBS process-loopback capture a stream the Helper renders to a **non-default** endpoint?
   (S1.) If not, the silent-Sink story becomes "OBS Audio Input Capture on a cable endpoint".
   **Answered YES, 2026-09-07:** operator ran `--tone --endpoint "VoiceMeeter Aux"` with an
   OBS Application Audio Capture on the Helper window; the meter moved. Recorded in ADR-0001.
2. Does a Harmony postfix on `MirrorIgnoranceClient.SendUnreliable(ArraySegment<byte>)` fire
   under IL2CPP for the host's own packets, and does the Dissonance VoiceData frame parse as
   documented? (S2.)
3. Can mod code obtain a game `IVoiceDataProvider` it controls without implementing an IL2CPP
   interface from managed code? (S3.) Preferred answer: instantiate the game's own
   `LocalVoiceProvider`, unsubscribe it from the mic, and push decoded PCM through its public
   `ReceiveMicrophoneData(ArraySegment<float>, WaveFormat)`. Fallback: Il2CppInterop interface
   injection.

## Tasks

### T0 — Core library and test project

- Add `src/TravelEar.Core` (`net6.0`, no BepInEx or game references): `DissonanceFrame` parser,
  `VoiceRingBuffer` (single-writer single-reader float ring), `SinkFrame` pipe framing
  (header: magic, channels, sample rate, capture timestamp, sample count; payload float32).
- Add `tests/TravelEar.Tests` (xunit, `net8.0`) referencing Core. Add `tests` to
  `[scan].roots`. Wire `dotnet test` in `scripts/gates.ps1` (already conditional on `tests/`).
- Unit tests: frame parser against a hand-built VoiceData packet per the documented protocol;
  ring buffer wrap-around and silence-on-underrun; SinkFrame round-trip.
- Tag: `[impl->REQ-VOICE-OUTBOUND-TAP]` on the parser, `[unit->…]` on its tests.

### T1 — Spike S1: Helper renders, OBS isolates it

- Helper: minimized window titled "TravelEar for Big Walk"; `--endpoint <substring>` selects
  the WASAPI render endpoint (empty = default); `--tone` renders a 440 Hz test tone so the spike
  needs no game; otherwise reads `SinkFrame`s from named pipe `TravelEar.Sink` and renders via
  NAudio `WasapiOut` (event-driven, shared mode) on a dedicated thread; exits when the pipe
  closes.
- Operator verification (needs OBS): add Application Audio Capture for the Helper window with
  `--tone --endpoint <a non-default device>`; confirm the OBS meter moves. Record the answer to
  open question 1 in this plan and in ADR-0001's consequences.
- Tag: `[impl->REQ-SINK-HELPER-PROCESS]`, `[impl->REQ-SINK-ENDPOINT-CONFIG]` on the Helper.

### T2 — Spike S2: Outbound Voice tap

- Plugin: `GameSymbols` bind step that resolves every class/method the mod needs from the
  interop assemblies and fails the whole plugin with one log line if any is missing (groundwork
  for `REQ-HAZARD-NO-PARTIAL-FIDELITY`).
- Harmony postfix on `MirrorIgnoranceClient.SendUnreliable`; copy the bytes, parse with
  `DissonanceFrame`, log sequence number, channel list, and payload length at debug level.
- Operator verification: host a solo session, speak with push-to-talk, read
  `BepInEx/LogOutput.log`. Confirms open question 2.
- Tag: `[impl->REQ-VOICE-OUTBOUND-TAP]` on the patch.

### T3 — Spike S3: mod-owned provider feeding a game VoicePlayer

- Decode tapped payloads with the game's `OpusDecoder(WaveFormat, fec)` into PCM.
- Obtain a provider per open question 3; create a mod-owned GameObject with a game
  `VoicePlayer` (`PlayerType = Clean`) whose `SampleProvider` is that provider; append a Tap
  filter that copies into `VoiceRingBuffer` and zeroes the buffer; write ring contents to the
  Helper pipe.
- Operator verification: with the Helper on the default device, speak in a solo session and hear
  Local Voice through the Helper only (not through the game). Confirms open question 3 and
  gives the first end-to-end Local Voice.
- Tag: `[impl->REQ-VOICE-ROUNDTRIP]`; `[impl->REQ-TAP-DIVERT]` on the Tap filter (activate
  `REQ-TAP-DIVERT` at that point with a unit test on the zeroing).

### T4 — Close-out

- Record spike answers in this plan and, where they change a decision, in the ADRs.
- Add `impl`/`unit` stages to the activated reqs; `scripts/gates.ps1` green.
- Write `M2-PLAN.md` (Mixer Stage re-synthesis, megaphone, Offset) just-in-time.

## Status log

- **T0 done** (`a39711d`, 2026-09-07). `TravelEar.Core` + 27 tests. Found and fixed a
  traceability trap: the CLI has no built-in C# scanner, so `.cs` tags vanished until
  `[scan.extensions] ".cs" = "c_like"` was added (now rule 7 in `docs/TRACEABILITY.md`).
- **T1 code done** (2026-09-07). Helper renders `--tone` or Sink frames from the pipe
  (`PipeDirection.In` client; the mod will be the server), minimized status window, exit codes
  0 ok / 1 bad args / 2 no endpoint / 3 audio failure, log at `%LOCALAPPDATA%\TravelEar\Helper.log`.
  Smoke-tested without OBS: unknown `--endpoint` exits 2 and lists devices; a scripted pipe
  server sending 100 silent frames to the VoiceMeeter Aux endpoint renders and the Helper exits 0
  when the pipe closes. Open question 1 answered YES by the operator (OBS meter moves on a
  non-default endpoint). **T1 done.**

- **T2 code done** (2026-09-07). `GameSymbols.Bind` resolves
  `Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceClient.SendUnreliable(ArraySegment<byte>)`
  (signature confirmed with ilspycmd on the interop assembly) and disables the mod with one
  error line on any miss. `OutboundVoiceTap` = Harmony postfix: copies the segment, parses with
  `DissonanceFrame`, logs the first 5 frames at Info (seq, session, sender, channel session,
  channels, payload bytes), then a summary every 250 packets; rejects at Warning; raises
  `FrameTapped` for T3. Plugin references `TravelEar.Core`; `DeployToGame` copies both DLLs.
  Deployed to the game. **Awaiting operator: open question 2** (host a solo session, PTT, read
  `BepInEx\LogOutput.log` for `Tap: VoiceData seq=...` lines).

## Gate

`pwsh scripts/gates.ps1` green: build, `dotnet test`, `traceable-reqs check` with the M1
requirements at their final stages, `mdbook build docs-site`.
