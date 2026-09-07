# TravelEar for Big Walk

TravelEar is a Big Walk mod that lets you hear and record your own voice the way other players
in your session hear it.

Big Walk runs your microphone through Opus, then through the game's own processing on every
listener's machine: makeup gain, distance and angle filtering, occlusion, reverb, and item
effects like the megaphone. You never hear any of that yourself. TravelEar renders that
processed voice from your own position and streams it to a separate Windows audio source that
OBS records as its own track.

**Status:** design complete, implementation in progress. Nothing here is released yet.

- [Install and record with OBS](install.md) takes you from a fresh BepInEx install to a
  separate OBS track.
- [Settings](settings.md) lists every configuration key.
- [How TravelEar hears you](how-it-works.md) explains what is exact, what is approximated, and
  why.

Source and issues: [github.com/SaberMage/travelear](https://github.com/SaberMage/travelear).
