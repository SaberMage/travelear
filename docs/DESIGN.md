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
                                                  ▼ Dissonance OpusDecoder
                                        round-trip PCM provider (IVoiceDataProvider)
                                                  │
                                                  ▼
                                   Local Voice renderer (mod-owned GameObject)
                                   ├─ game VoicePlayer (Clean | Megaphone, follows held item)
                                   ├─ game VoiceMakeupGain
                                   ├─ Self-Ear curve values (distance 0, angle 0, occlusion 0)
                                   └─ Tap: last IAudioFilter copies buffer, then zeroes it
                                                  │
                                                  ▼
                                   Mixer Stage re-synthesis (mod DSP, driven by AudioMixer.GetFloat)
                                                  │
                                                  ▼ named pipe, float32 48 kHz, N channels
                                          Helper process (WASAPI render)
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

Implements the game's `IVoiceDataProvider` ring-buffer contract (the same interface `SamplePlaybackComponent` and `LocalVoiceProvider` implement) so any game `VoicePlayer` can consume it unchanged. Emits silence when no packets arrive, so the stream never gaps.

### Local Voice renderer

A mod-owned GameObject holding a game `VoicePlayer` whose `PlayerType` follows the local player's held item (`Clean` when nothing relevant is held, `Megaphone` when the megaphone is held). Fed by the round-trip provider, never by `LocalVoiceProvider`. The game's own local self-voice `VoicePlayer` instances are left untouched.

<!-- [doc->REQ-TAP-DIVERT] -->
The Tap sits at the end of the Filter Stage: a Harmony postfix on the source's `AudioFilterMixer.OnAudioFilterRead`, filtered to the renderer's own mixer. It copies the processed buffer into a lock-free ring for the Sink and then zeroes the buffer in place, so the game's mixer receives silence from this source. The zeroing is unconditional: a full ring drops samples, it never lets audio through.

Self-Ear parameters: evaluate the game's attenuation, filter-distance, filter-angle, and spatial curves at distance 0 and angle 0, occlusion 0, and read the local player's own `outdoorness` and `echoAmount`. Apply `VoiceMakeupGain` exactly as `PlayerVoicePlaybackControl.Update` does for a remote player.

Self-Ear geometry (operator note, 2026-09-07, for later experimentation): a person does not hear their own voice on-axis. The voice leaves at the edges of the mouth and through the cheeks, so from the speaker's own ears it radiates roughly perpendicular, as a cone of about 160-170 degrees whose apex sits 2-3 inches in front of the ears. A peer voice pointed straight at the listener sits at angle 0 on the game's filter-angle curve; the Self-Ear should therefore probably sit off-axis on that curve (some extra `High{n}` roll-off relative to a peer facing you) rather than at angle 0. Treat the angle-0 value above as the v1 starting point and calibrate the off-axis amount by ear against a second-client recording.

### Mixer Stage re-synthesis

Reads the same per-channel mixer floats the game writes (`Dry{n}`, `High{n}`, `ReverbFallWet{n}`, `ReverbBoostWet{n}`, `Megaphone{n}Wet/Dry`, HP/LP, compressors) and applies equivalent DSP in the mod. Each effect has a config toggle. Reverb is approximated; calibrate against a recording made on a second client hearing the same speech.

<!-- [doc->REQ-SINK-HELPER-PROCESS] -->
<!-- [doc->REQ-SINK-ENDPOINT-CONFIG] -->
### Sink transport and Helper

Mod side: named pipe server `TravelEar.Sink`, frames of float32 interleaved PCM at 48 kHz with a small header (channel count, capture timestamp). Helper side: .NET 8 self-contained WinExe, minimized window titled "TravelEar for Big Walk", NAudio `WasapiOut` on a dedicated thread to the endpoint matching config `SinkEndpoint` (empty = system default). Helper writes render timestamps back on the pipe so the mod can compute Offset.

Lifecycle: mod spawns the Helper once per session outside the game's process tree (WMI `Win32_Process.Create`), retries the pipe every 5 s, never respawns in a loop. Helper exits when the pipe closes. Manual launch is supported.

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
3. Instantiate a game `VoicePlayer` from mod code with a mod-provided `IVoiceDataProvider`.

## Test setup

Second client (another machine or Steam account) records the same session; compare its playback of the local player against the Sink output for level, spectrum, and reverb tail. Test with the VR mod disabled first; its Steam Audio spatializer may colour voice.
