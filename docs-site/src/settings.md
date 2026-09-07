# Settings

All settings live in `Big Walk\BepInEx\config\com.sabermage.travelear.cfg`. If you have the
[ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/)
mod installed, the same settings appear in the pause menu.

| Key | Default | Meaning |
| --- | --- | --- |
| `General.Enabled` | `true` | Render your processed voice and stream it to the Helper. |
| `Sink.SpawnHelper` | `true` | Launch the Helper automatically with the game. Set to `false` if you start `TravelEar.Helper.exe` yourself. |
| `Sink.SinkEndpoint` | empty | Part of the name of the playback device the Helper renders to. Empty means the system default device. |
| `Sink.Downmix` | `false` | Force mono output. By default the stream keeps the channel count the game produces. |
| `Fidelity.MixerStage` | `true` | Re-synthesize the game's mixer-stage effects (reverb sends, dry/high trims, megaphone character). Turn off to hear only the exactly captured part. |

Restart the game after changing `SinkEndpoint` or `SpawnHelper`. The other keys apply live.
