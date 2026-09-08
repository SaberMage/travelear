# TravelEar for Big Walk

A [Big Walk](https://store.steampowered.com/app/1478500/Big_Walk/) mod that lets you hear and record your own voice the way other players in your session hear it.

Big Walk's proximity voice chat runs your mic through Opus, then through the game's own processing on every listener's machine: makeup gain, compression, the voice EQ, the room reverb of wherever you stand, the fall reverb, and item effects like the megaphone. You never hear any of that yourself. TravelEar renders that processed voice ("Local Voice") from your own position and streams it to a separate Windows audio source that OBS can record as its own track or monitor in your headphones. It is never mixed into the game's audio.

Status: v1.0 covers plain voice, environment and fall reverb, and the megaphone. See [CHANGELOG.md](CHANGELOG.md) and [docs/DESIGN.md](docs/DESIGN.md).
Full docs: [docs-site/src](docs-site/src/SUMMARY.md). Contributors and dev-agents start at
[AGENTS.md](AGENTS.md).

## How it works

1. The mod copies the exact Opus packets the game sends to other players.
2. It decodes them on the game's mic thread and applies the same processing a listener standing next to you gets: the game's remote-voice dynamics and EQ, then the mixer-stage effects re-synthesized from the same live parameters the game writes each frame (environment reverb, fall reverb, megaphone). See [ADR 0002](docs/adr/0002-mixer-stage-resynthesis.md) and [ADR 0005](docs/adr/0005-sink-fed-from-the-encoder-thread.md).
3. The result goes over a named pipe to a small Helper process, outside the game's process tree.
4. The Helper plays it to a Windows playback device of your choice. OBS captures the Helper with **Application Audio Capture**, so it gets its own track without any virtual cable driver.

## Requirements

- Big Walk on Windows (Steam), with [BepInEx 6 IL2CPP](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/) installed (build be.755 or newer).
- OBS Studio 28 or newer on Windows 10 2004 / Windows 11 for Application Audio Capture.
- Optional: [ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/) to change settings from the pause menu.

## Install

1. Download `TravelEar-vX.Y.Z.zip` from the [latest release](https://github.com/SaberMage/travelear/releases/latest).
2. Extract it into the Big Walk folder (the one that contains `Big Walk.exe` and `BepInEx\`). The zip carries its own `BepInEx\` tree, so you end up with:
   - `BepInEx\plugins\TravelEar\TravelEar.dll` and `TravelEar.Core.dll`
   - `BepInEx\TravelEar.Helper\TravelEar.Helper.exe` (kept outside `plugins` on purpose)
3. Launch the game once. `BepInEx\config\com.sabermage.travelear.cfg` is created and the Helper starts; a minimized window titled **TravelEar for Big Walk** appears in your taskbar.

### OBS

1. Add a source: **Application Audio Capture**.
2. Set **Window** to **TravelEar for Big Walk** and tick **Match by executable**.
3. In **Advanced Audio Properties** route the source to its own track and record with that track enabled.

### Choosing the output device (`Sink.SinkEndpoint`)

By default the Helper plays to your system default output, so you hear Local Voice about 200 ms after you speak. To keep it silent, set `Sink.SinkEndpoint` to part of the name of a playback device you are not listening to (a spare HDMI output, VB-CABLE, a VoiceMeeter input) and restart the game. To monitor on demand instead, leave it empty and use the OBS source's **Audio Monitoring** setting.

`TravelEar.Helper.exe --tone [--endpoint "CABLE Input"]` plays a 440 Hz test tone so the OBS capture can be checked without the game.

### Offset

Local Voice trails your raw mic by the delay the mod measures continuously: the last row of **Settings > Audio** reads `TravelEar offset: N ms` (main menu and pause menu), and `BepInEx\LogOutput.log` carries an `Offset:` line every 10 s. Enter that figure as the **Sync Offset** on your raw mic source in OBS if you want both tracks aligned. Expect roughly 200 ms.

## Settings

All settings live in `BepInEx\config\com.sabermage.travelear.cfg`; ModSettingsMenu lists them titled by key. The full table with every `Fidelity.*` toggle is in [docs-site/src/settings.md](docs-site/src/settings.md). The ones most people touch:

| Key | Default | Meaning |
| --- | --- | --- |
| `General.Enabled` | `true` | Render Local Voice and stream it to the Helper. |
| `Sink.SinkEndpoint` | empty | Playback device the Helper renders to. Empty = system default. |
| `Sink.SpawnHelper` | `true` | Launch the Helper automatically with the game. |
| `Sink.Downmix` | `false` | Force mono output. |
| `Fidelity.EnvironmentReverb` | `true` | The room reverb listeners hear on your voice, driven by the game's live reverb parameters. |
| `Fidelity.MixerReverbFall` | `true` | The reverb listeners hear while you fall outdoors. |
| `Fidelity.MegaphoneVoice` | `true` | Render the megaphone's output while you hold and use one. |
| `Fidelity.TransmitGate` | `true` | Render Local Voice only while peers receive it; off renders the mic noise floor too. |
| `Ear.SelfEarForwardMeters` | `0.0762` | How far in front of your in-game ears the voice is placed (3 in). |

## Building

```
dotnet build TravelEar.sln -c Release
```

The plugin references the Il2CppInterop proxy assemblies in `Big Walk/BepInEx/interop/`, which exist after the game has been launched once with BepInEx. Set `GameDir` in a git-ignored `Directory.Build.props.user` if your install is elsewhere. `-p:DeployToGame=true` copies the plugin and the Helper into the game.

The Helper is published self-contained as a single file:

```
dotnet publish src/TravelEar.Helper -c Release
```

It logs to `%LOCALAPPDATA%\TravelEar\Helper.log`.

Before declaring work done, run every gate:

```
pwsh scripts/gates.ps1
```

Releases are assembled by `pwsh scripts/release.ps1` per [docs/RELEASE-RUNBOOK.md](docs/RELEASE-RUNBOOK.md). Requirements are tracked in `traceable-reqs.toml` and gated by
[traceable-reqs](https://github.com/BigscreenVR/traceable-reqs); see [docs/TRACEABILITY.md](docs/TRACEABILITY.md).

## Scope

- v1.0: plain voice, environment and fall reverb, megaphone.
- v1.1: cliff echo, underwater muffle.
- Not planned: walkie-talkie (the game already plays your own voice through nearby walkies) and radio (it plays music, not voice).

## License

MIT. See [LICENSE](LICENSE).
