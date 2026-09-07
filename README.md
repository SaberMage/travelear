# TravelEar for Big Walk

A [Big Walk](https://store.steampowered.com/app/1478500/Big_Walk/) mod that lets you hear and record your own voice the way other players in your session hear it.

Big Walk's proximity voice chat runs your mic through Opus, then through the game's own processing on every listener's machine: makeup gain, distance and angle filtering, occlusion, reverb, and item effects like the megaphone. You never hear any of that yourself. TravelEar renders that processed voice ("Local Voice") from your own position and streams it to a separate Windows audio source that OBS can record as its own track or monitor in your headphones. It is never mixed into the game's audio.

Status: design complete, implementation not started. See [docs/DESIGN.md](docs/DESIGN.md).
Full docs: [docs-site/src](docs-site/src/SUMMARY.md). Contributors and dev-agents start at
[AGENTS.md](AGENTS.md).

## How it works

1. The mod copies the exact Opus packets the game sends to other players.
2. It decodes them and plays them through the game's own voice playback components, configured as if a listener stood at your position.
3. The processed audio is captured before it reaches the speakers and sent to a small Helper process.
4. The Helper renders it to a Windows playback device of your choice. OBS captures the Helper with **Application Audio Capture**, so it gets its own track without any virtual cable driver.

Effects the game applies inside Unity's audio mixer (reverb sends, megaphone character) cannot be captured directly and are re-synthesized by the mod from the same live parameters. See [ADR 0002](docs/adr/0002-mixer-stage-resynthesis.md).

## Requirements

- Big Walk on Windows (Steam), with [BepInEx 6 IL2CPP](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/) installed.
- OBS Studio 28 or newer on Windows 10 2004 / Windows 11 for Application Audio Capture.
- Optional: [ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/) to change settings from the pause menu.

## Install

1. Download the latest release zip.
2. Extract `TravelEar/` into `Big Walk/BepInEx/plugins/`.
3. Launch the game once. `BepInEx/config/com.sabermage.travelear.cfg` is created.
4. In OBS add **Application Audio Capture**, pick the window "TravelEar for Big Walk", and route it to its own track.

By default the Helper renders to your system default output, so you will hear Local Voice a few dozen milliseconds after you speak. To keep it silent, set `SinkEndpoint` to a substring of an unused playback device (a spare HDMI output, VB-CABLE, VoiceMeeter input).

## Settings

| Key | Default | Meaning |
| --- | --- | --- |
| `General.Enabled` | `true` | Render Local Voice and stream it to the Sink. |
| `Sink.SpawnHelper` | `true` | Launch the Helper automatically with the game. |
| `Sink.SinkEndpoint` | empty | Playback device the Helper renders to. Empty = system default. |
| `Sink.Downmix` | `false` | Force mono output. |
| `Fidelity.MixerStage` | `true` | Re-synthesize the game's mixer-stage effects. |

The measured delay between your mic and the Sink is logged every few seconds and, where supported, shown in the pause menu's Audio section. Use it as the OBS sync offset if you need the track aligned with a raw mic track.

## Building

```
dotnet build TravelEar.sln -c Release
```

The plugin references the Il2CppInterop proxy assemblies in `Big Walk/BepInEx/interop/`, which exist after the game has been launched once with BepInEx. Set `GameDir` in a git-ignored `Directory.Build.props.user` if your install is elsewhere. `-p:DeployToGame=true` copies the plugin into the game's plugins folder.

The Helper is published self-contained:

```
dotnet publish src/TravelEar.Helper -c Release
```

`TravelEar.Helper.exe --tone [--endpoint <substring>]` renders a 440 Hz test tone so the OBS
capture can be checked without the game. It logs to `%LOCALAPPDATA%\TravelEar\Helper.log`.

Before declaring work done, run every gate:

```
pwsh scripts/gates.ps1
```

Requirements are tracked in `traceable-reqs.toml` and gated by
[traceable-reqs](https://github.com/BigscreenVR/traceable-reqs); see [docs/TRACEABILITY.md](docs/TRACEABILITY.md).

## Scope

- v1.0: plain voice and megaphone.
- v1.1: cliff echo, underwater muffle.
- Not planned: walkie-talkie (the game already plays your own voice through nearby walkies) and radio (it plays music, not voice).

## License

MIT. See [LICENSE](LICENSE).
