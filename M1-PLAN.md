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
   documented? (S2.) **Answered 2026-09-07:** postfixes fire and the parse is byte-exact, but a
   solo host sends no VoiceData and generic-class postfixes never fire; the tap now sits on
   `OpusEncoder.Encode`. Details in the status log.
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
  Deployed to the game.
- **T2 in-game runs 1-4** (2026-09-07, solo host each time):
  1. Bind ok, detour installed, zero tap lines.
  2. Canaries on `SendReliable`/`Send`: HandshakeRequest + ClientState seen, bytes big-endian and
     session id byte-exact with Dissonance's log => **Harmony postfixes fire under IL2CPP and the
     `DissonanceFrame` layout is right**. `SendUnreliable` never called: a host with no listeners
     builds no VoiceData packet.
  3. `BaseClient<...>.SendVoiceData` postfix: never fired either (generic base class; IL2CPP
     generic sharing suspected of routing calls past the detour).
  4. `VoiceBroadcastTrigger` probe: the game's own "Self Echo" trigger (mode Open, room `Echo`)
     keeps a room channel open all session; VAD fires on the `GhostRoom` trigger; local player
     `IsSpeaking=true`. So transmit IS on solo; the send-side hooks were the wrong place.
     Also seen: `RadioBroadcastTriggers` (Open, rooms RingRoom1-4, MegaphoneA-C,
     MegaphoneSecretZone, Interviewer, InterviewSubject, CenturionSeance, RadioA, TrainIntercom;
     only open with the token), `VoiceColliderTrigger` (VoiceActivation, type Self).
  5. Non-generic `OpusEncoder.Encode(samples, buffer)` postfix: **fires** (2880 samples in,
     75-138 bytes out per frame, hundreds of frames while speaking) while the generic
     `SendVoiceData` postfix stayed silent in the same run. **Open question 2 answered; T2 done.**
     Tap point moved to the encoder output (DESIGN.md updated); spike classes removed; the
     `DissonanceFrame` parser stays in Core as tested reference code.

- **T3 bodies read** (2026-09-07, Cpp2IL `dll_il_recovery` + `callanalyzer` + `diffable-cs` + ISIL;
  recipe in the project memory). What the game actually does, and what the code relies on:
  - `VoicePlayer.Awake`: `_cachedClip = AudioClip.Create("Voice Player", bufferLength*numBuffers, 1,
    outputSampleRate, stream:true, pcm => fill(1.0f))`; megaphone index parsed from the Cue name.
  - `VoicePlayer.OnEnable` (needs a live `Cue`, else no-op): `_controller = AudioPlayHelper.Play(Cue,
    zero, owner:this, null, followTransform:transform, rtpc:true, 0, fade const, _cachedClip, GetX,
    false, null)`; `AddEvent(_onStop, clearRef)`; `AddVolume(Volume, this)`;
    `_controller._filterSynthesizerMode = true` (+ its `AudioFilterMixer.SynthesizerMode`);
    **`AddFilter(this, 0)`**; BitCrusher/Biquad/megaphone mixers only for Radio/Megaphone/Walkie/
    SelfVoice; then `_readHead = SampleProvider.RecommendedVoiceReadHead`. Clean = nothing extra.
  - `VoicePlayer.Update`: if `Cue` alive and `_controller` dead → destroy clip, `Awake()`, `OnEnable()`
    (self-heals after a pooled controller stop). Megaphone block writes the `Megaphone*` mixer
    floats; Walkie block does water depth. Clean does nothing per frame.
  - `VoicePlayer.ProcessSamples(ref data, channels)`: `data[i] = provider.CachedVoiceData[_readHead++ & (len-1)]`
    for every interleaved sample (no channel logic: the provider ring is already interleaved).
  - `set_SampleProvider`: stores as `LocalVoiceProvider` or `SamplePlaybackComponent`, syncs
    `_readHead`, subscribes `OnWriteHeadJump` → resync. `get_SampleProvider` = whichever is alive.
  - `AudioFilterMixer.OnAudioFilterRead`: if `SynthesizerMode`: `_cachedData = data; data = 0`; run
    every `Filters[i].ProcessSamples(ref data, channels)`; then `data[i] = clamp(_cachedData[i] * data[i], -1, 1)`
    (non-synth: clamp only). So the constant-1.0 clip carries Unity's spatial/attenuation gain and
    the mixer multiplies the voice by it: the postfix sees the complete Filter Stage output.
  - `LocalVoiceProvider`: `Awake` sizes `CachedVoiceData` to the next power of two of
    `bufferLength*numBuffers*channelCount` (channelCount from `AudioSettings.speakerMode`);
    `ReceiveMicrophoneData` locks the ring, pins `_format` on first call (later mismatch throws),
    duplicates each mono sample to `channelCount` interleaved slots, no resampling;
    `RecommendedVoiceReadHead = writeHead - 2*bufferLength*numBuffers*channelCount` (mod len);
    `Start` = `DissonanceComms.SubscribeToRecordedAudio(this)`; `Update` only fires `OnWriteHeadJump`.
  - `PlayerVoicePlaybackControl` (remote voices) takes its cue from a stack built from
    `GlobalAudioEffects.Instance.VoiceCues` (mixer group name = voice channel number 1..N).
    `EchoRemote`/`RadioVoiceAssigner` set `VoicePlayer.SampleProvider` on prefab-carried players.
  - `AudioSourceController` layout: `_filterMixer` (+0x110), `_filters` (+0x118),
    `_filterSynthesizerMode` (+0x128), `_onStop` (+0x150); interop exposes `FilterMixer`/`Filters`.
- **T3 code done** (2026-09-07), pending the in-game run: `LocalVoiceDecoder` (game `OpusDecoder`
  48 kHz mono FEC, decodes into an IL2CPP float array), `RoundTripProvider` (mod-owned
  `LocalVoiceProvider` on an inactive GameObject; Harmony prefix on `LocalVoiceProvider.Start` skips
  the mic subscription for that instance only; decoded PCM pushed through the
  `IMicrophoneSubscriber.ReceiveMicrophoneData` proxy on the encoder thread), `LocalVoiceRenderer`
  (injected `TravelEarBehaviour` builds the renderer once `GlobalAudioEffects`/`AudioManager` exist:
  `VoicePlayer` Clean, `Cue = VoiceCues[last]`, `Volume = new AudioVolume(1)`, provider field set
  before activation; follows `AudioManager.ListenerPosition`; re-arms the Tap whenever
  `Controller.FilterMixer` changes), `TapFilter` (postfix on `AudioFilterMixer.OnAudioFilterRead`
  filtered by mixer pointer; `TapDivert.Divert` copies to the Sink ring and zeroes in place;
  `REQ-TAP-DIVERT` activated with unit tests), `SinkPump` (pipe server `TravelEar.Sink`, sends
  whatever the Tap produced per 5 ms tick, re-arms on disconnect), `HelperLauncher`
  (`Process.Start` spike; `Sink.HelperPath` config; `DeployToGame` now copies the Helper build
  output to `plugins\TravelEar\Helper`). Stats line every 10 s at Info.

- **T3 in-game runs** (2026-09-07):
  1. Crash (AV in coreclr) on the first decoded frame: the interop-generated `Nullable<T>(T)`
     ctor passes the boxed pointer for a struct-proxy `T`; fixed by building the
     `Nullable<ArraySegment<byte>>` payload by memory copy (`LocalVoiceDecoder.WrapNullable`).
     Also moved the Helper out of `plugins` (BepInEx examined all 260 files).
  2. **Open question 3 answered YES; first end-to-end Local Voice.** Operator heard himself
     through the Helper (after restarting a wedged VoiceMeeter engine; the Helper renders to the
     system default device unless `SinkEndpoint` is set). Log: 1033 frames decoded/pushed, Tap
     peaks 0.4 while speaking, 4475 Sink frames. Findings: (a) ~800-900 ms lag; (b) emitter
     lagged the camera (louder in the opposite ear while strafing) because the pooled source
     follows our transform through the game's follow logic one frame late; (c) stereo
     artifacts when still, emitter at the exact listener point.
  3. Fixes for the next run: emitter parented rigidly to the `AudioListener` at
     `SelfEarForwardMeters` (default 3 in) with `FollowTransform` cleared (REQ-EAR-SELF
     activated doc+impl); provider read head resynced 1.5 frames behind the write head at each
     talk-burst start plus a lag guard (the game's read head phase was random per burst: up to
     341 ms); Helper ring backlog clamped (trim to 30 ms when above 80 ms; `VoiceRingBuffer.Discard`
     + tests). Stats line now shows provider lag in ms and resyncs; "Game audio" line shows
     listener/master/mute state and our source.

  4. Run 3: lag and panning mostly as predicted, but the emitter pin never happened:
     `Object.FindObjectOfType(Type)` is stripped in this IL2CPP build ("Method unstripping failed",
     thrown 16k times) and the tick swallowed it, so the source stayed at the build-time listener
     position (felt like a fixed world-axis offset). Fixed: anchor = `Camera.main.transform`
     (the game's `AudioListenerController` follows the main camera), fallback
     `AudioManager.Instance.ListenerController._listener`; warning logged once if neither exists;
     position follows the listener while unpinned. Root cause of the "raised noise floor
     in-world": Outbound Voice exists only while transmitting, so after a burst the VoicePlayer
     kept looping the last 341 ms of the ring (mostly mic noise floor) forever. Fixed by
     `KeepRingFresh`: zero frames pushed between bursts to keep the write head ahead of the
     reader (REQ-VOICE-CONTINUOUS impl). Note for later fidelity work: the remote-voice path adds
     `SamplePlaybackComponent` compression, soft clip, `VoiceMakeupGain`, ARV gating and
     `PlayerVoicePlaybackControl` EQ/attenuation that the Clean `LocalVoiceProvider` path skips
     (M2: REQ-RENDER-CLEAN / REQ-EAR-SELF curves).

### T3 signature notes (from the interop assemblies, 2026-09-07)

- `VoicePlayer : MonoBehaviour` is itself the `IAudioFilter` (`ProcessSamples(ref
  Il2CppStructArray<float> data, int channels)`, `UpdateVariables(float)`); fields
  `LocalVoiceProvider LocalVoiceProvider`, `SamplePlaybackComponent SamplePlaybackComponent`,
  `VoicePlayerType PlayerType`, `AudioSourceController _controller`, `SoundCue Cue`,
  `int _readHead`, `UpdateReadHead(int)`, `_bypass`, megaphone/walkie mixers.
- Provider contract (game `IVoiceDataProvider`): `Il2CppStructArray<float> CachedVoiceData`,
  `int CachedVoiceWriteHead`, `int RecommendedVoiceReadHead`, `Action OnWriteHeadJump`.
  `LocalVoiceProvider : MonoBehaviour` implements it plus `IMicrophoneSubscriber` via the public
  proxy method `Dissonance_Audio_Capture_IMicrophoneSubscriber_ReceiveMicrophoneData(ArraySegment<float>, WaveFormat)`;
  `WorldManager.instance.localVoiceProvider` is the game's own instance.
- `AudioFilterMixer : MonoBehaviour { List<IAudioFilter> Filters; OnAudioFilterRead(Il2CppStructArray<float>, int) }`
  => the Tap can be a Harmony postfix on `OnAudioFilterRead` filtered to the mod-owned mixer
  instance (copy then zero), no managed IL2CPP interface needed.
- `AudioSourceController.AddFilter(IAudioFilter, int index = -1)`, `RemoveFilter`, `Play(SoundCue,
  Vector3, Object owner, ...)`, `SetupController(...)`, `Initialize()`.
- `OpusDecoder(WaveFormat format, bool fec = true)`, `int Decode(EncodedBuffer input,
  ArraySegment<float> output)`; `EncodedBuffer(Nullable<ArraySegment<byte>> encoded, bool
  packetLost)`; `NAudio.Wave.WaveFormat(int sampleRate, int channels)` (inside DissonanceVoip).
  Session codec: Opus, FrameSize 2880 samples, 48 kHz, FEC on (from the Dissonance start log).
- `SamplePlaybackComponent` (remote players' provider): `MakeupGain`, `ARV`, `OutputARV`,
  `_compressor` (VoiceCompressor), soft-clip constants, `CachedVoiceData`.
- `VoiceMakeupGain` static: `s_states: Dictionary<string, State>`, `TargetARV`, slew/settle
  constants; `State { Envelope, Level, SpeechSeconds, GainDb }`.
- `LocalVoicePlayer` = the game's self-voice player (mic cache based); leave untouched.
- Also present: `LocalVoiceSaver : MonoBehaviour, IMicrophoneSubscriber` with an
  `AudioSampleSaver` (a game debug feature that writes the mic feed; not used by us).

## Gate

`pwsh scripts/gates.ps1` green: build, `dotnet test`, `traceable-reqs check` with the M1
requirements at their final stages, `mdbook build docs-site`.
