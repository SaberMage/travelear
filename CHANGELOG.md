# Changelog

All notable user-facing changes to TravelEar are recorded here. The section for a tagged
version becomes that GitHub Release's body verbatim.

## [Unreleased]

First release. TravelEar renders your own Big Walk voice the way the other players in your
session hear it and streams it to a separate Windows audio source ("Local Voice") that OBS
records as its own track. Nothing is mixed into the game's audio.

### Added

- **Local Voice end to end.** The exact Opus packets the game sends to other players are
  decoded on your machine and passed through the same processing a listener standing next to
  you gets: makeup gain, compressor and soft clip, the game's voice EQ, then rendered from just
  in front of your in-game ears.
- **The listener's gain chain.** The game's indoor voice attenuation (6 dB down fully indoors,
  `Fidelity.IndoorAttenuation`), the red bells' voice fade (`Fidelity.SpeechlessVolume`) and
  the master limiter every listener's mix ends in (`Fidelity.MasterLimiter`) are applied as the
  game does. The bells' pitch drop and reverb bloom are not rendered yet.
- **Environment reverb.** The room reverb other players hear on your voice (hallways, caves,
  almost nothing outdoors) follows the same live reverb parameters the game writes every frame,
  including the game's own dry copy and bus levels. `Fidelity.EnvironmentReverb` and its
  `EnvironmentReverb*` toggles switch each part off for comparison.
- **Fall reverb** (off by default). The game's fall reverb send can be applied to Local Voice
  with `Fidelity.MixerReverbFall`; it is off because that send belongs to a voice receding from
  its listener, and your own voice never recedes from your own ears.
- **Megaphone.** Pick up a megaphone and use it: the crushed, thinned and squashed megaphone
  voice, with its mixer's low-pass, EQ, reverb and echo, is rendered on top of your direct voice
  while you broadcast, as a listener beside you hears it. `Fidelity.MegaphoneMix = Replace` keeps
  only the megaphone output.
- **Transmit gate.** Local Voice is rendered only while other players would receive it (not
  muted, and the game's voice activation hears speech or a radio/megaphone room is open), so the
  mic noise floor between words never reaches the track. The gate opens and closes with the
  game's own channel fade, not a cut. The room reverbs keep ringing after the gate closes, as
  they do on a listener's machine. `Fidelity.TransmitGate` turns it off,
  `Fidelity.TransmitFadeOutMs` overrides the fade-out, `Fidelity.TransmitHoldMs` how long it
  stays open after speech, and `Fidelity.OutputTrimDb` trims the final level.
- **The Helper.** A small separate process, started with the game, plays Local Voice to a
  Windows playback device of your choice. In OBS, add **Application Audio Capture** and pick the
  window "TravelEar for Big Walk" to record it on its own track with no virtual cable driver.
  By default it plays to your system default output; `Sink.SinkEndpoint` names another device
  (a spare HDMI output, VB-CABLE, a VoiceMeeter input) if you want it silent. `Sink.Downmix`
  folds it to mono. The Helper's counters go to `%LOCALAPPDATA%\TravelEar\Helper.log` every 10 s.
- **Offset.** The delay from your mic to the Helper's output is measured continuously and
  shown as a read-only "TravelEar offset: N ms" row at the bottom of **Settings > Audio** (main
  menu and pause menu), and logged as `Offset:` in `BepInEx\LogOutput.log`. Enter it as the OBS
  **Sync Offset** on your raw mic source to align the two tracks. Expect roughly 200 ms.
- **Settings.** Every option lives in `BepInEx\config\com.sabermage.travelear.cfg` and shows up
  in ModSettingsMenu, titled by key. `General.Enabled` switches the whole mod off.
- **Test tone.** `TravelEar.Helper.exe --tone [--endpoint <part of a device name>]` plays a
  440 Hz tone so the OBS capture can be checked without launching the game.

### Requirements

- Big Walk on Windows (Steam) with BepInEx 6 IL2CPP, build be.755 or newer.
- OBS Studio 28 or newer on Windows 10 2004 / Windows 11 for Application Audio Capture.
