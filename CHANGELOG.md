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
