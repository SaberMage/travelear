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
| `Fidelity.MixerStage` | `true` | Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character). Turn off to hear only the exactly captured part. |
| `Fidelity.TransmitGate` | `true` | Render your voice only while other players receive it (a voice-activation or push-to-talk channel is open). Off renders everything the mic encodes, noise floor included. |
| `Fidelity.ReadHeadMarginFrames` | `1.5` | Buffer margin at the start of each talk burst, in 60 ms frames. Lower for less delay; raise it if the log's stats line shows read-head resyncs climbing. |
| `Fidelity.SelfEarEqDryWet` | `0` | Wet mix (0 to 1) of the game's 400 Hz voice EQ. 0 is the dry voice a listener standing next to you hears. |
| `Ear.SelfEarForwardMeters` | `0.0762` | How far in front of your in-game ears the voice is placed, in metres (3 inches). |

`Sink.Downmix` applies live. Restart the game after changing any other key.
