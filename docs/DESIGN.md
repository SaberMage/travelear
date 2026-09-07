# TravelEar design

Vocabulary is defined in [CONTEXT.md](../CONTEXT.md). Decisions with real trade-offs are in [docs/adr](./adr).

## Goal

Give the local Big Walk player a recordable audio stream of their own voice as other players hear it: same encoded bytes, same in-game processing, rendered from the Self-Ear (zero distance, facing the listener, no occlusion, the player's own outdoorness).

## Pipeline

```
mic ─► Dissonance preprocess ─► Opus encode ─► Mirror send ──────────────────► peers
                                                  │
                                                  ▼ (Harmony postfix, copy bytes)
                                        Outbound Voice tap
                                                  │
                                                  ▼ game OpusDecoder (encoder thread)
                                   round-trip provider (mod-owned game LocalVoiceProvider, mic skipped)
                                                  │
                                                  ▼
                                   Local Voice renderer (mod-owned GameObject under the listener)
                                   ├─ game VoicePlayer, Clean (Megaphone when held: later)
                                   ├─ emitter SelfEarForwardMeters ahead of the AudioListener
                                   ├─ remote-path processing: makeup gain, compressor, soft clip (M2 T1); voice EQ wet mix by config
                                   └─ Tap: postfix on AudioFilterMixer.OnAudioFilterRead copies, then zeroes
                                                  │
                                                  ▼
                                   Mixer Stage re-synthesis (mod DSP, driven by AudioMixer.GetFloat)
                                                  │
                                                  ▼ named pipe, float32 48 kHz, N channels
                                   Helper process (BepInEx\TravelEar.Helper, WASAPI render)
                                                  │
                                                  ▼
                            OBS Application Audio Capture  →  own track / monitoring
```

## Components

<!-- [doc->REQ-VOICE-OUTBOUND-TAP] -->
### Outbound Voice tap (`TravelEar` plugin)

Harmony postfix on Dissonance's `OpusEncoder.Encode(samples, buffer)`. Its return value is the exact encoded frame the game goes on to send (mic audio after the game's capture preprocessing, then Opus), so it is Outbound Voice by definition, and it fires whenever the local player transmits whether or not any peer is listening. Push-to-talk, voice activation, and FEC state are reproduced by construction because the encoder only runs while the game transmits. The tap copies the bytes and hands them, with a local sequence number, to the decoder.

Settled by M1 spike S2 (five in-game runs): a host with no listeners never builds a VoiceData packet, so a postfix on the Mirror client's send sees nothing solo; a postfix on the generic `BaseClient<...>.SendVoiceData` installs but never fires under IL2CPP generic sharing; `OpusEncoder.Encode` is non-generic and upstream of both. Rule of thumb for every later hook: prefer non-generic methods. The documented VoiceData wire format (magic `0x8BC7`, type, session, sender, flags, sequence, channel list, Opus payload; all big-endian) was confirmed byte-exact against live packets and stays implemented and unit-tested in `TravelEar.Core.DissonanceFrame` as reference code.

<!-- [doc->REQ-VOICE-ROUNDTRIP] -->
### Round-trip provider

A mod-owned instance of the game's own `LocalVoiceProvider` ([ADR-0003](./adr/0003-game-voiceplayer-and-local-voice-provider.md)). It already implements the `IVoiceDataProvider` ring contract every game `VoicePlayer` consumes, so no IL2CPP interface is implemented from managed code. It lives on the renderer's GameObject; a Harmony prefix on `LocalVoiceProvider.Start`, keyed on that instance's pointer, skips the mic subscription for it alone, so it never carries raw mic audio and the game's own provider is untouched. Tapped frames are decoded on the encoder thread with the game's `OpusDecoder` (48 kHz mono, FEC on, 2880-sample frames) and pushed through the provider's public `IMicrophoneSubscriber.ReceiveMicrophoneData` proxy exactly as the mic feed would be. The provider duplicates mono to the DSP channel count and does not resample; the mod warns if the DSP rate is not 48 kHz.

Read-head discipline: the game syncs a `VoicePlayer`'s read head to its provider once at enable time, so with talk bursts the phase between the two is random (up to a full ring, 341 ms of pure latency). The renderer resyncs the read head 1.5 frames behind the write head at each burst start, and again whenever the lag drifts past 4 frames or under a quarter frame mid-burst. Ring lag then sits steady around 150-180 ms.

<!-- [doc->REQ-VOICE-CONTINUOUS] -->
Continuity: Outbound Voice exists only while the game transmits, but a `VoicePlayer` reads its provider's ring forever. Between bursts the renderer pushes zero frames from the main thread so the write head stays ahead of the read head; without this the player loops the last ring's worth of the burst, mostly mic noise floor (M1 T3 runs 2-3). Silence is therefore what the Sink carries whenever no packets arrive, from game launch on. In practice the encoder rarely stops: push-to-talk defaults to toggle-on and the game's "Self Echo" room channel is always open, so Outbound Voice carries the mic noise floor between words. The transmit gate (M2 T0, `Fidelity.TransmitGate`, default on) limits Local Voice to what peers actually receive: each tick the renderer reads the Dissonance comms and treats "peers receive" as: not muted, and either a room other than the always-open `Echo` room is open (the token rooms: radio, megaphone, GhostRoom once its channel opens) or a voice-activation `VoiceBroadcastTrigger` reports its VAD speaking. The VAD flag carries the signal because the peer-facing channels are voice-activation triggers whose channel only opens with a peer to send to; in a solo session it never opens while the VAD still flips with speech (M2 runs 1-2). While "peers receive" is false, the frame fades out and then zero frames of the decoded frame's length are pushed instead of the frame itself. The fade-in and fade-out are the game's own (the `VolumeFaderSettings` on its voice-activation trigger, the fade a peer hears on that channel, applied ahead of the remote path's dynamics; config `Fidelity.TransmitFadeOutMs` overrides the fade-out) and the gate's release hold covers the fade-out, so a flickering VAD ramps the voice instead of splicing zero frames into it (M2 run 3). The gate never drops a frame, so the ring's continuity does not depend on it; a signal that cannot be read fails open (everything renders) rather than silencing Local Voice.

### Local Voice renderer

A mod-owned GameObject (`TravelEar.LocalVoice`, built on the main thread once `GlobalAudioEffects` and `AudioManager` exist) holding the round-trip provider and a game `VoicePlayer` with `PlayerType = Clean`, `Cue` = the last entry of `GlobalAudioEffects.Instance.VoiceCues` (the cue kind the game hands remote players, so spatial settings, attenuation and RTPCs match), `Volume` 1, and the provider assigned before activation. The `VoicePlayer` then does what it does for the game's own voices: plays the cue through `AudioPlayHelper.Play` with a constant-1.0 streaming clip, puts itself at index 0 of the pooled `AudioSourceController`'s filter list, switches the source's `AudioFilterMixer` into synthesizer mode (so the mixer multiplies the voice by the source's spatial gain), and re-plays itself if the pooled controller is reclaimed. `PlayerType` following the held item (`Megaphone`) is a later milestone. The game's own self-voice `VoicePlayer` instances are left untouched.

<!-- [doc->REQ-TAP-DIVERT] -->
The Tap sits at the end of the Filter Stage: a Harmony postfix on `AudioFilterMixer.OnAudioFilterRead`, filtered to the renderer's own mixer by object pointer; the renderer re-arms it whenever the controller's mixer changes. It copies the processed buffer into a lock-free ring for the Sink and then zeroes the buffer in place, so the game's mixer receives silence from this source. The zeroing is unconditional: a full ring drops samples, it never lets audio through. It runs on Unity's audio thread: no allocation, no logging.

<!-- [doc->REQ-EAR-SELF] -->
Self-Ear parameters: the emitter (the pooled `AudioSource` the `VoicePlayer` plays through) sits a short distance straight ahead of the `AudioListener` along the view axis (config `SelfEarForwardMeters`, default 3 in / 0.0762 m). The renderer's object and the pooled source are both parented to an anchor at that same local offset, and the controller keeps following the renderer's object: its per-frame position write then resolves to the exact local offset whatever anchor pose it read, so the audio thread always sees the current view with no lag. The anchor is the camera above the listener when one exists, else the listener object itself (`Camera.main` is null in this game; the listener comes from `AudioManager.Instance.ListenerController._listener`, never `Object.FindObjectOfType`, which is stripped). Rigid parenting is required: the game's follow logic alone updates the source one frame behind the camera, which flips left/right while strafing; a source at the exact listener position produces stereo artifacts; and clearing the follow target drops the source at the world position it was played at (M1 T3 runs 2-5). The pooled source's original parent is restored when the controller goes away. The remote path's per-voice processing is applied to each decoded frame before it enters the provider (next paragraph). The game's 400 Hz voice EQ, which remote voices fade in from distance and angle curves, is pinned to config `SelfEarEqDryWet` (default 0: at the Self-Ear both curve inputs are zero, and the mod does not read the curve assets, which live on a prefab only a remote player instantiates) and is attached to the pooled source only when that is above 0, since at 0 the filter is an exact passthrough and a component left on a pooled source follows it to its next owner. Occlusion is 0 by construction. The local player's own `outdoorness` and `echoAmount` and the attenuation/spatial curves feed the Mixer Stage (M3), not this path.

<!-- [doc->REQ-RENDER-CLEAN] -->
Remote-path processing: every remote voice passes through `SamplePlaybackComponent.ProcessSamples` (a per-sample gain ramp to the makeup gain, the `VoiceCompressor`, a hyperbolic soft clip with a 0.95 ceiling), driven once per frame by `VoiceMakeupGain.Evaluate` (an automatic gain control toward a mean level of 0.132: 24 dB/s for the first second of a burst, then 12 dB/s up and 1 dB/s down, frozen between bursts). The Clean `LocalVoiceProvider` path has none of this, so the mod ports it (Core `VoiceDynamics`, `VoiceCompressor`, `VoiceMakeupGain`, unit-tested against the constants in docs/reference/big-walk-voice-dsp.md) and applies it on the encoder thread to each decoded frame before the provider push. The loop runs on the main thread from the last block's input level, with the game's `TargetARV` and compressor threshold read, never written, so the in-game voice volume slider couples exactly as it does for peers; the game's per-player `VoiceMakeupGain` dictionary is not used because it is keyed by player name and shared. A burst start resets the processing state the way a new speech session does for a remote voice; the makeup-gain state persists across bursts, as the game's does.

Self-Ear geometry (operator note, 2026-09-07, for later experimentation): a person does not hear their own voice on-axis. The voice leaves at the edges of the mouth and through the cheeks, so from the speaker's own ears it radiates roughly perpendicular, as a cone of about 160-170 degrees whose apex sits 2-3 inches in front of the ears. A peer voice pointed straight at the listener sits at angle 0 on the game's filter-angle curve; the Self-Ear should therefore probably sit off-axis on that curve (some extra `High{n}` roll-off relative to a peer facing you) rather than at angle 0. Treat the angle-0 value above as the v1 starting point and calibrate the off-axis amount by ear against a second-client recording.

### Mixer Stage re-synthesis

Reads the same per-channel mixer floats the game writes (`Dry{n}`, `High{n}`, `ReverbFallWet{n}`, `ReverbBoostWet{n}`, `Megaphone{n}Wet/Dry`, HP/LP, compressors) and applies equivalent DSP in the mod. Each effect has a config toggle. Reverb is approximated; calibrate against a recording made on a second client hearing the same speech.

<!-- [doc->REQ-SINK-HELPER-PROCESS] -->
<!-- [doc->REQ-SINK-ENDPOINT-CONFIG] -->
### Sink transport and Helper

Mod side: named pipe server `TravelEar.Sink`, frames of float32 interleaved PCM at the DSP rate with a small header (channel count, sample rate, capture timestamp, sample count); the pump sends whatever the Tap produced per 5 ms tick and never pads on its own. Helper side: .NET 8 self-contained WinExe, minimized window titled "TravelEar for Big Walk", NAudio `WasapiOut` on a dedicated thread to the endpoint matching config `SinkEndpoint` (empty = system default, which is audible); it primes 100 ms of silence at stream start and trims its backlog to 100 ms whenever it exceeds 200 ms (the band is the jitter budget: below it the stream pads or cuts mid-word, M2 runs 4-5), and logs its underrun and trim counters every 10 s to `%LOCALAPPDATA%\TravelEar\Helper.log`.

<!-- [doc->REQ-OFFSET-MEASURE] -->
Offset: measured end to end, per Sink frame, on one clock (both processes read the machine's performance counter). The capture point is the `OpusEncoder.Encode` postfix; the render point is the moment the frame's first sample leaves the Helper's endpoint. The timestamp rides the audio through every ring hand-off by position, not by counting: each writer marks the span it wrote (ring position, length, timestamp) in a small lock-free table (`TravelEar.Core.FrameStampTable`) and the reader on the other thread resolves the position it is about to read to the newest mark containing it. Three hand-offs: the provider ring (every push marks, voice with its encode timestamp and mod-generated silence with "none", so the Tap can resolve the block it is handed from the `VoicePlayer`'s read head, modulo the ring, whatever resyncs did to it), the Sink ring (Tap to pump, monotonic positions; the pump stamps the frame header with the resolved timestamp or 0 for none), and the Helper's ring (pipe thread to WASAPI render thread; render time = now plus what the endpoint still has queued ahead of that sample, from the output's audio clock, or the nominal 40 ms latency if the clock is unavailable). The Helper sends `(capture, render)` pairs back on a second, inbound-only pipe `TravelEar.Sink.Back` (20-byte records, `OffsetReportFrame`) so the frame stream is untouched; the mod keeps a rolling 10 s window (`OffsetAverager`) and logs `Offset: N ms rolling 10 s average` every 10 s. The largest tunable term is the read-head margin at each talk burst, config `Fidelity.ReadHeadMarginFrames` (default 1.5 frames of 60 ms): lower it until read-head resyncs appear in the stats line, then back off. Reports are dropped, never queued without bound, when the return pipe is absent.

<!-- [doc->REQ-SINK-LIFECYCLE] -->
Lifecycle: the Helper ships at `BepInEx\TravelEar.Helper\TravelEar.Helper.exe`, beside and not inside `plugins`, because BepInEx examines every DLL under `plugins` as a plugin candidate. The mod spawns it once per game launch (config `SpawnHelper`, `HelperPath`) and never respawns it, whether the spawn failed or the Helper later exited; a Helper already running (started by hand or by an earlier launch) is left alone. The spawn goes through the Helper's own `--detach` step: the process the mod starts re-executes itself without the flag and exits at once, so the Helper that stays up is the child of a short-lived launcher rather than of the game, and OBS's process-tree matching cannot fold its audio into the game's capture (M2 chose this over WMI `Win32_Process.Create` because it needs no extra assembly under the game's runtime). The pipe server re-arms after every disconnect but never more often than every 5 s, and every Sink-side failure is absorbed with a log line. The Helper exits when the pipe closes. Manual launch is supported. Policy in `TravelEar.Core.HelperLifecycle`, unit-tested.

<!-- [doc->REQ-SINK-FORMAT] -->
Format: float32 interleaved PCM at the DSP rate (48 kHz expected) with the Tap's channel count, normally stereo. With config `Downmix` on, the pump folds each block to mono (equal-weight average of the channels, `TravelEar.Core.Downmixer`) before framing, and the header says 1 channel; the Helper follows the header, so the switch is transparent to it.

### Config and settings UI

BepInEx config entries (auto-surfaced as toggles by ModSettingsMenu if present). A read-only "TravelEar offset: N ms" row is cloned into the game's Audio settings category via `SettingsRow` if that proves stable; Offset is also logged as a rolling 10 s average.

## Behaviour rules

- Never affects gameplay or what the local player hears.
- Stream is always on from game launch; silence when not speaking or not in a session.
- If any hook fails to bind after a game update: disable, log once, stay silent. No partial-fidelity output.
- Channel count matches the Tap (expected stereo); `Downmix` config forces mono.

## Scope

- v1.0: Clean Voice, Megaphone Voice, Mixer Stage reverb/dry/high, Offset display.
- v1.1: cliff echo (`EchoRemote` equivalent), water-depth muffle.
- Out: walkie-talkie and radio (the game already plays the local player's walkie voice through nearby units; the radio plays music).

## First spikes

1. OBS process-loopback capture of a stream rendered to a non-default endpoint. **Answered yes** (M1, 2026-09-07).
2. Harmony postfix on the Dissonance send path under IL2CPP; confirm frame parse. **Answered** (M1, 2026-09-07): postfixes fire; the frame parse is byte-exact; the tap moved upstream to `OpusEncoder.Encode` (see the tap section).
3. Instantiate a game `VoicePlayer` from mod code with a mod-provided `IVoiceDataProvider`. **Answered yes** (M1, 2026-09-07): a mod-owned instance of the game's own `LocalVoiceProvider` (mic subscription skipped by a Harmony prefix) fed through its `IMicrophoneSubscriber` proxy drives a mod-owned Clean `VoicePlayer`; the operator heard Local Voice end to end through the Helper. No IL2CPP interface implemented from managed code.

## Test setup

Second client (another machine or Steam account) records the same session; compare its playback of the local player against the Sink output for level, spectrum, and reverb tail. Test with the VR mod disabled first; its Steam Audio spatializer may colour voice.
