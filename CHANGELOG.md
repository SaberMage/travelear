# Changelog

All notable user-facing changes to TravelEar are recorded here. The section for a tagged
version becomes that GitHub Release's body verbatim.

## [Unreleased]

### Added

- Project bootstrapped: design, glossary, and working rules.
- M1: Local Voice end to end. Your own voice, as the game encodes it for peers, is decoded and
  rendered from just in front of your in-game ears and streamed to the TravelEar Helper, which
  OBS can capture as its own source. Clean voice only; the game's remote-voice compressor, EQ,
  and reverb are not applied yet, and the mic noise floor between words is audible.
- M2 T0: transmit gate. Local Voice is rendered only while peers actually receive it (a
  voice-activation or push-to-talk channel is open), so the mic noise floor between words no
  longer reaches the Helper. `Fidelity.TransmitGate` turns it off for comparison.
- M2 T3: Helper lifecycle and Sink format. The Helper is spawned once per game launch, outside
  the game's process tree (so OBS cannot fold it into the game's capture), and never respawned;
  the Sink pipe re-arms no more often than every 5 s. `Sink.Downmix` now works: Local Voice is
  folded to mono before it reaches the Helper.
- M2 T2: Offset measurement. The delay from your mic (as the game encodes it) to the Helper's
  output is measured end to end and logged every 10 s as a rolling average, ready to use as the
  OBS sync offset. `Fidelity.ReadHeadMarginFrames` tunes the largest part of that delay.
