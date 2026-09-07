# Local Voice rides the game's own LocalVoiceProvider and VoicePlayer, not a managed provider

## Status

accepted (2026-09-07)

## Context

1. The **Local Voice** renderer needs an `IVoiceDataProvider` that a game `VoicePlayer` will
   consume. That interface is IL2CPP; implementing it from managed code means Il2CppInterop
   interface injection, which is fragile and undocumented for the generic-struct proxies the
   game's audio types use.
2. The game already ships two implementations: `SamplePlaybackComponent` (remote players, fed
   by the network decoder) and `LocalVoiceProvider` (the local player's self-voice, fed by the
   mic through `IMicrophoneSubscriber`). Cpp2IL bodies (M1-PLAN, "T3 bodies read") gave the
   ring semantics of `LocalVoiceProvider` exactly: power-of-two ring, mono duplicated to the DSP
   channel count, no resampling, format pinned on first call, read head recommended two DSP
   buffer sets behind the write head.
3. A game `VoicePlayer` does all the playback wiring itself on enable: plays its `Cue` through
   `AudioPlayHelper.Play` with a constant-1.0 streaming clip, installs itself at index 0 of the
   pooled `AudioSourceController`'s filter list, switches the source's `AudioFilterMixer` into
   synthesizer mode, and re-plays itself if the pooled controller is reclaimed. Re-creating that
   in the mod would copy game behaviour we would rather inherit.
4. Spike S3 had to answer, in one milestone, whether mod code can own a provider at all.

## Decision

The renderer is a mod-owned GameObject carrying a mod-owned instance of the game's
**`LocalVoiceProvider`** and a game **`VoicePlayer`** (`PlayerType = Clean`, `Cue` = the last
`GlobalAudioEffects.Instance.VoiceCues` entry, `Volume` 1). A Harmony prefix on
`LocalVoiceProvider.Start`, keyed on the owned instance's pointer, skips the mic subscription for
that instance only. Decoded **Outbound Voice** (the game's `OpusDecoder`, 48 kHz mono, FEC on) is
pushed through the provider's public `IMicrophoneSubscriber.ReceiveMicrophoneData` proxy on the
encoder thread. The **Tap** is a Harmony postfix on `AudioFilterMixer.OnAudioFilterRead`
filtered to the renderer's mixer by object pointer.

Rejected: a managed `IVoiceDataProvider` via Il2CppInterop interface injection (never needed;
kept as the fallback if a game update breaks `LocalVoiceProvider`'s public surface); a mod-owned
`AudioSource` with its own `OnAudioFilterRead` (skips the game's per-voice filter list, cue
settings and RTPCs, so the Filter Stage would no longer be the game's).

## Consequences

- The Clean path is exactly the game's own self-voice chain, but **not** the remote-player
  chain: `SamplePlaybackComponent`'s compressor, soft clip, `VoiceMakeupGain` and ARV, and
  `PlayerVoicePlaybackControl`'s distance/angle/spatial curves and EQ are absent. M2 adds them
  on top of our `VoicePlayer`'s controller (`REQ-RENDER-CLEAN`, `REQ-EAR-SELF`).
- Ring discipline belongs to the mod: the game syncs the read head once at enable, so the
  renderer resyncs it 1.5 frames behind the write head at every burst start and zero-fills the
  ring between bursts (`REQ-VOICE-CONTINUOUS`). The provider does not resample; a DSP rate other
  than 48 kHz pitch-shifts and is warned about.
- The pooled `AudioSourceController` can be reclaimed at any time; the renderer re-arms the Tap
  whenever the controller's mixer changes and restores the pooled source's parent when it goes.
- Il2CppInterop generic-struct pitfall (paid for with an AV in coreclr, T3 run 1): the
  interop-generated `Nullable<T>(T)` constructor passes the boxed pointer for a struct-proxy
  `T`. Build `Nullable<ArraySegment<byte>>`, and any other generic over a struct proxy, by
  memory copy (`LocalVoiceDecoder.WrapNullable`).
- `Object.FindObjectOfType(Type)` is stripped from this IL2CPP build (T3 run 3). Locate game
  objects through the game's singletons: `AudioManager.Instance.ListenerController._listener`,
  `WorldManager.instance`, `GlobalAudioEffects.Instance`. `Camera.main` is null in this game.
- Hook non-generic methods only: postfixes on generic classes install but never fire under
  IL2CPP generic sharing (spike S2).
- Standing rule of thumb: wherever a game type already satisfies a contract the mod needs, own
  an instance of it rather than reimplementing the contract in managed code.
