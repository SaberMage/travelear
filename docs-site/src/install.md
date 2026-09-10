# Install and record with OBS

You need Big Walk on Windows with BepInEx 6 (IL2CPP) already installed, and OBS Studio 28 or
newer on Windows 10 2004 or Windows 11.

## 1. Install the mod

1. Download `TravelEar-vX.Y.Z.zip` from the
   [latest release](https://github.com/SaberMage/travelear/releases/latest).
2. Extract it into the Big Walk folder (the one that contains `Big Walk.exe` and `BepInEx\`).
   The zip carries its own `BepInEx\` tree, so you end up with
   `BepInEx\plugins\TravelEar\TravelEar.dll`, `BepInEx\plugins\TravelEar\TravelEar.Core.dll`
   and `BepInEx\TravelEar.Helper\TravelEar.Helper.exe`. The Helper sits outside `plugins` on
   purpose (BepInEx would otherwise scan it as a plugin); `Sink.HelperPath` points elsewhere if
   you move it.
3. Launch Big Walk once. The mod writes `BepInEx\config\com.sabermage.travelear.cfg` and
   starts the Helper. A minimized window titled **TravelEar for Big Walk** appears in your
   taskbar.

## 2. Add the track in OBS

1. In OBS, add a source: **Application Audio Capture**.
2. Set **Window** to **TravelEar for Big Walk** and tick **Match by executable**.
3. Open **Advanced Audio Properties** and route the new source to its own track (for example
   track 3). Record with that track enabled.

Speak in a session. The new source's meter moves about 200 ms after your own mic meter.

## 3. Choose whether you hear it

By default the Helper plays to your system default output device, so you hear your processed
voice with a short delay. To record it silently, set `SinkEndpoint` in the config to part of the
name of a playback device you are not listening to, such as a spare HDMI output, VB-CABLE, or a
VoiceMeeter input. Restart the game after changing it.

To monitor on demand instead, leave `SinkEndpoint` empty, set **Audio Monitoring** on the OBS
source to **Monitor Off**, and switch it to **Monitor and Output** when you want to hear
yourself.

## Testing the OBS capture without the game

Run the Helper by hand with a test tone to confirm your OBS source hears it before you set up
a session:

```
TravelEar.Helper.exe --tone
TravelEar.Helper.exe --tone --endpoint "CABLE Input"
```

`--endpoint` takes part of a playback device name, exactly like `SinkEndpoint`. If no device
matches, the Helper lists the active devices in its window and in
`%LOCALAPPDATA%\TravelEar\Helper.log`, then exits. Close the Helper window to stop the tone.

## 4. Align with your raw mic track

TravelEar's track trails your raw mic by the Offset the mod measures continuously. Read it
from the Helper's window (`TravelEar offset: N ms`; restore "TravelEar for Big Walk" from the
taskbar; `measuring` until the Helper has streamed) or from `BepInEx\LogOutput.log` (a line
like `Offset: 203 ms rolling 10 s average`), and enter that value as the **Sync Offset** on your
raw mic source if you want both tracks sample-aligned for editing. Expect roughly 200 ms.
