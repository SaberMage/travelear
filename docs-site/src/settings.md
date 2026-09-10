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
| `Fidelity.MixerReverbFall` | `false` | The game's fall reverb send, applied to Local Voice. Off by default: that send belongs to a voice receding from the listener, and your own voice never recedes from your own ears. Kept for comparison. |
| `Fidelity.MixerReverbBoost` | `true` | The game's reverb boost send. Silent at your own ears by the game's own formula; kept for comparison. |
| `Fidelity.ReverbDecaySeconds` | `1.5` | Decay time of the approximate reverb, in seconds. Tune by ear against a recording from a second player. |
| `Fidelity.MegaphoneVoice` | `true` | Render the megaphone's output while you hold and use one, as a listener beside you hears it. Master switch for the `Megaphone*` toggles. |
| `Fidelity.MegaphoneCrusher` | `true` | The megaphone's sample-hold crusher. Exact port of the game's. |
| `Fidelity.MegaphoneHighPass` | `true` | The megaphone's 300 Hz high-pass. Exact port. |
| `Fidelity.MegaphoneCompressors` | `true` | The megaphone mixer's compressor (10 ms / 1 s) and its Duck Volume (-15 dB, 5:1, 0.25 s). Approximate. |
| `Fidelity.MegaphoneMix` | `Add` | `Add` puts the megaphone output on top of your direct voice, which is what a listener beside you hears. `Replace` keeps only the megaphone output while you broadcast. |
| `Fidelity.EnvironmentReverb` | `true` | The room reverb a listener beside you hears on your voice (the game's dynamic reverb: hallways, caves, almost nothing outdoors), driven by the same live parameters the game writes each frame. Master switch for the `EnvironmentReverb*` toggles. Levels, onsets and decay are exact targets; the reverb network is approximate. |
| `Fidelity.EnvironmentReverbDryCopy` | `true` | The reverb's own un-reverbed copy of your voice, which the game mixes on top of the direct path. It is what makes a nearby voice sit in the room rather than beside it. |
| `Fidelity.EnvironmentReverbBusGains` | `true` | The fixed bus trims a voice meets on a listener's machine (-3 dB voice group, -6 dB dry bus). Off = both at 0 dB, louder than the game. |
| `Fidelity.EnvironmentReverbVoiceSlider` | `false` | Multiply by the listener's voice volume slider as the game does. Off by default: the Sink level already follows your own slider. |
| `Fidelity.IndoorAttenuation` | `true` | The game's indoor voice attenuation: a listener hears every voice at `Outdoorness * 0.5 + 0.5`, 6 dB down fully indoors. Uses your own outdoorness at the Self-Ear. |
| `Fidelity.SpeechlessVolume` | `true` | The red bells' voice fade: a speaker inside a speechless zone is heard at `1 - speechlessness`, silent at the centre. The zone's pitch drop and super-wet bloom are not rendered yet. |
| `Fidelity.SpeechlessPitch` | `true` | The red bells' pitch drop: the voice mixer's pitch shifter at the `VoicePitch` the game writes for the listener (`1 - speechlessness * the zone's deduction`). Pitch without tempo, as in the game; adds 16 ms of latency inside Local Voice. Off = no shifter in the chain. |
| `Fidelity.SpeechlessBloom` | `true` | The red bells' super-wet bloom: the 6.8 s dark reverb and chorus that opens as the listener nears a zone (`SuperWet_Speechlessness`, pitched by `SuperWetPitch`, both read from the game). Approximate reverb and chorus. |
| `Fidelity.MasterLimiter` | `true` | The game's master limiter, the last thing a listener's mix goes through: -3 dB threshold, 10:1, 0.125 s release, 20 dB knee. Approximate detector. |
| `Fidelity.MegaphoneMixer` | `true` | The megaphone mixer's fixed effects beside its dynamics: 5 kHz low-pass, 2.5 kHz +8 dB EQ, a 2 s -10 dB reverb and a 100 ms echo. Approximate reverb. |
| `Fidelity.TransmitGate` | `true` | Render your voice only while other players would receive it: not muted, and the game's voice activation hears speech or a radio/megaphone room is open. Off renders everything the mic encodes, noise floor included. |
| `Fidelity.TransmitFadeOutMs` | `0` | Fade-out of Local Voice when the game's voice activation stops hearing you, in ms. `0` = the game's own channel fade, read from its voice-activation trigger and logged as `Transmit fade:`. Raise it if speech still chops between words. |
| `Fidelity.TransmitHoldMs` | `0` | How long Local Voice keeps rendering after the game's voice activation stops hearing you, before the fade-out starts, in ms. `0` = automatic (the fade-out plus 60 ms, at least 100; logged as `gate hold`). Raise it if the quiet ends of words get cut. |
| `Fidelity.OutputTrimDb` | `0` | Gain applied to Local Voice last, just before the Helper, in dB. Calibration only: the chain's level is the game's (a listener beside you gets the -6 dB dry bus plus the reverb's own dry copy, about +1 dB together). Negative = quieter. |
| `Fidelity.ReadHeadMarginFrames` | `1.5` | `VoicePlayer` feed only. Buffer margin at the start of each talk burst, in 60 ms frames. Lower for less delay; raise it if the log's stats line shows read-head resyncs climbing. |
| `Fidelity.SelfEarEqDryWet` | `0` | Wet mix (0 to 1) of the game's 400 Hz voice EQ. 0 is the dry voice a listener standing next to you hears. |
| `Calibration.Capture` | `false` | Reference capture for calibrating the mod against a real listener (M3-PLAN T4a): records the decoded outbound voice, the Local Voice output, every frame's gate disposition and every mixer float the game writes under `%LOCALAPPDATA%\TravelEar\calibration\<timestamp>`. Off unless you are measuring. |
| `Calibration.CaptureDevice` | empty | Part of the name of the Windows capture device carrying the other machine's audio output (an HDMI capture card, line-in). With `Capture` on, a second Helper records it to `peer.wav` beside the capture; `tools/calibrate.py` compares the three. Empty = record it yourself. |
| `Ear.SelfEarForwardMeters` | `0.0762` | How far in front of your in-game ears the voice is placed, in metres (3 inches). |

`Sink.Downmix` applies live. Restart the game after changing any other key.

## Offset

The Helper's window ("TravelEar for Big Walk", minimized to the taskbar) shows
`TravelEar offset: N ms`: the delay between your voice leaving the mic and reaching the
Helper's output, as a rolling 10 s average. It reads `measuring` until the first frames have
streamed. The same figure is logged as `Offset:` every 10 s. TravelEar adds nothing to the
game's own menus.
