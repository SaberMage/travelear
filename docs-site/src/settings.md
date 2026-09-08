# Settings

All settings live in `Big Walk\BepInEx\config\com.sabermage.travelear.cfg`. If you have the
[ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/)
mod installed, the same settings appear in the pause menu.

| Key | Default | Meaning |
| --- | --- | --- |
| `General.Enabled` | `true` | Render your processed voice and stream it to the Helper. |
| `Sink.SpawnHelper` | `true` | Launch the Helper automatically with the game. Set to `false` if you start `TravelEar.Helper.exe` yourself. |
| `Sink.SinkEndpoint` | empty | Part of the name of the playback device the Helper renders to. Empty means the system default device. |
| `Sink.HelperPath` | empty | Full path to `TravelEar.Helper.exe`. Empty means the copy under `BepInEx\TravelEar.Helper`. |
| `Sink.Downmix` | `false` | Force mono output. By default the stream keeps the channel count the game produces. |
| `Fidelity.SinkFeed` | `Encoder` | Where your voice is taken from. `Encoder` processes the decoded outbound voice on the game's mic thread and sends it straight to the Helper: lowest delay, and immune to the game's audio-thread hiccups. `VoicePlayer` is the older path through an in-game voice player, kept for comparison. |
| `Fidelity.MixerStage` | `true` | Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character). Master switch for the `Mixer*` toggles below. Turn off to hear only the exactly reproduced part. |
| `Fidelity.MixerDry` | `true` | The game's per-voice dry level. 0 dB at your own ears; kept for comparison. |
| `Fidelity.MixerHigh` | `true` | The game's occlusion high cut, approximated as a 3 kHz shelf. Nothing to cut at your own ears; kept for comparison. |
| `Fidelity.MixerReverbFall` | `true` | The reverb other players hear on your voice while you fall outdoors. Approximate reverb. |
| `Fidelity.MixerReverbBoost` | `true` | The game's reverb boost send. Silent at your own ears by the game's own formula; kept for comparison. |
| `Fidelity.ReverbDecaySeconds` | `1.5` | Decay time of the approximate reverb, in seconds. Tune by ear against a recording from a second player. |
| `Fidelity.MegaphoneVoice` | `true` | Render the megaphone's output while you hold and use one, as a listener beside you hears it. Master switch for the `Megaphone*` toggles. |
| `Fidelity.MegaphoneCrusher` | `true` | The megaphone's sample-hold crusher. Exact port of the game's. |
| `Fidelity.MegaphoneHighPass` | `true` | The megaphone's 300 Hz high-pass. Exact port. |
| `Fidelity.MegaphoneCompressors` | `true` | The megaphone mixer's two compressors. Approximate. |
| `Fidelity.MegaphoneMix` | `Add` | `Add` puts the megaphone output on top of your direct voice, which is what a listener beside you hears. `Replace` keeps only the megaphone output while you broadcast. |
| `Fidelity.TransmitGate` | `true` | Render your voice only while other players would receive it: not muted, and the game's voice activation hears speech or a radio/megaphone room is open. Off renders everything the mic encodes, noise floor included. |
| `Fidelity.TransmitFadeOutMs` | `0` | Fade-out of Local Voice when the game's voice activation stops hearing you, in ms. `0` = the game's own channel fade, read from its voice-activation trigger and logged as `Transmit fade:`. Raise it if speech still chops between words. |
| `Fidelity.ReadHeadMarginFrames` | `1.5` | `VoicePlayer` feed only. Buffer margin at the start of each talk burst, in 60 ms frames. Lower for less delay; raise it if the log's stats line shows read-head resyncs climbing. |
| `Fidelity.SelfEarEqDryWet` | `0` | Wet mix (0 to 1) of the game's 400 Hz voice EQ. 0 is the dry voice a listener standing next to you hears. |
| `Ear.SelfEarForwardMeters` | `0.0762` | How far in front of your in-game ears the voice is placed, in metres (3 inches). |

`Sink.Downmix` applies live. Restart the game after changing any other key.
