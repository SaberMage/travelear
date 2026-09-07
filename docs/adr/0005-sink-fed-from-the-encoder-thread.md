# The Sink is fed from the encoder thread; the in-game VoicePlayer path is an A/B fallback

## Status

accepted (2026-09-07) · supersedes the feed-point parts of [ADR-0003](0003-game-voiceplayer-and-local-voice-provider.md)
(the round-trip provider, the Tap and the read-head discipline stay implemented behind config
`Fidelity.SinkFeed = VoicePlayer`; the rest of ADR-0003 stands)

## Context

1. M1-M2 fed the Sink through the game: decoded Outbound Voice pushed into a mod-owned
   `LocalVoiceProvider` ring, read by a game `VoicePlayer` (`Clean`, synthesizer mode) on
   Unity's audio thread, captured by the **Tap** (a postfix on `AudioFilterMixer.OnAudioFilterRead`)
   and zeroed. That path cost the ring lag (150-200 ms of the 320-360 ms Offset), the read-head
   resync discipline, and, from M2 runs 5-6, the game's own audio-thread stalls (~250 ms about
   80 s into a session), which peers never hear because the game's encoder runs on the mic thread.
2. The M3 T0 bodies read (docs/reference/big-walk-local-voice-wiring.md, section 3) lists what
   a `Clean` `VoicePlayer` in synthesizer mode adds between the ring read and the source output:
   `ring[readHead++]` assignment, no type-specific filter, then `clamp(envelope * data, -1, 1)`,
   where the envelope is the source's gain chain (spatial gain at the Self-Ear's ~3 in, cue
   volumes) at near unity. It writes no mixer floats. The remote-path processing a peer applies
   (`SamplePlaybackComponent`, `PlayerVoicePlaybackControl`'s EQ) was already ported to Core in M2
   because the `VoicePlayer` path never had it. The only game DSP the mod attached itself was the
   400 Hz voice EQ (`BiquadFilters` PeakingEQ, config `SelfEarEqDryWet`, default 0), whose kernel
   is fully documented (docs/reference/big-walk-voice-dsp.md section 5).
3. The Mixer Stage (ADR-0002) is mod DSP driven by mixer floats either way, so it has no stake in
   where the samples come from.

## Decision

The Sink's input is the encoder thread. After the game's decoder, the transmit gate and fade, and
the remote path's dynamics, the renderer writes each frame (mono, 48 kHz, stamped with its encode
timestamp) straight into a Sink ring the pump drains (`SinkFeed`). The voice EQ is a Core port of
the game's peaking filter (`PeakingEq`, same RBJ coefficients, wet/dry mix and clamp), applied on
the same thread when `SelfEarEqDryWet` is above 0. No GameObject, provider, `VoicePlayer` or Tap
is created. The Helper gains a re-prime rule (`StarveGuard`): after its ring runs dry it holds on
silence until the backlog is back at the 100 ms target, because the encoder stops with the mic
(mute, menu) and a burst played from an empty ring would spend every arrival jitter as a click.

Config `Fidelity.SinkFeed` selects the feed point: `Encoder` (default) or `VoicePlayer` (the
ADR-0003 path, unchanged, for A/B in the operator's runs). The `VoicePlayer` path is removed once
a release has shipped on `Encoder` without a regression report.

Rejected: keeping the `VoicePlayer` path for the Self-Ear envelope (near unity, and the Self-Ear
is the mod's own construct, so its gain is a mod decision, not the game's); a mod-owned
`AudioSource` (ADR-0003 already rejected it, and it would keep the audio-thread stalls).

## Consequences

- Offset drops by the provider ring lag and the read-head margin; the Helper's 100 ms prime and
  the encode-to-decode hop remain. `Fidelity.ReadHeadMarginFrames` only applies to the
  `VoicePlayer` feed.
- The game's audio-thread stalls no longer reach the Sink; what remains in the Sink is what the
  mic thread produced.
- `REQ-TAP-DIVERT` and `REQ-HAZARD-NO-GAME-AUDIO-LEAK` hold by construction on the default path
  (nothing of Local Voice enters Unity's audio) and by the Tap's zeroing on the `VoicePlayer` path;
  `TapDivert` and its tests stay as the fallback's proof.
- The Self-Ear (`REQ-EAR-SELF`) is now purely the mod's model: the emitter offset and the game's
  follow logic are `VoicePlayer`-path only; the EQ wet mix and, later, the angle term are Core DSP.
- The Mixer Stage (M3 T1) and Megaphone voice (T2) run on the encoder thread too; the megaphone's
  in-process filters (`BitCrusher`, 300 Hz high-pass) become Core ports rather than attached
  components.
- Docs updated: DESIGN "Pipeline", "Round-trip provider", "Local Voice renderer", "Sink transport";
  CONTEXT "Tap"; KNOWN-HAZARDS 1.1.
