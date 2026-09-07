# TravelEar

A Big Walk mod that lets the local player hear (and record) their own voice the way other players in the session hear it.

## Language

**Outbound Voice**:
The local player's voice as the game transmits it to peers: mic audio after the game's capture preprocessing and Opus encoding.
_Avoid_: mic feed, raw mic, sidetone

**Local Voice**:
The local player's Outbound Voice decoded and rendered through the game's own voice playback processing (items, reverb, occlusion), as a peer would hear it.
_Avoid_: echo (a game audio bus name), sidetone, loopback, monitor

**Ear**:
The listener model used to render Local Voice. The Self-Ear is the local player's own in-game listener at zero distance from the speaker.
_Avoid_: virtual listener, observer

**Tap**:
The point in the game's voice pipeline where Local Voice is captured for the Sink instead of being mixed into game audio.
_Avoid_: hook, intercept

**Sink**:
The Windows-side audio stream that carries Local Voice out of the game so OBS can record it as its own track.
_Avoid_: virtual cable, output device, audio source

**Helper**:
The separate Windows process, outside the game's process tree, that renders the Sink so OBS can capture it per-application.
_Avoid_: bridge, daemon, sidecar

**Offset**:
The measured delay between the moment Outbound Voice leaves the mic and the moment Local Voice reaches the Sink.
_Avoid_: latency, lag, sync

## Rendering stages

**Filter Stage**:
The in-process per-voice DSP chain the game runs on each voice before it reaches the mixer: decode, compression, soft clip, EQ, occlusion, item colouring.
_Avoid_: filter chain, source effects

**Mixer Stage**:
The effects the game applies inside Unity's audio mixer after the Filter Stage (reverb sends, dry/high trims, megaphone character, compressors). The mod cannot capture these and must re-synthesize them.
_Avoid_: post-processing, mixer effects

**Clean Voice**:
A player's voice heard directly, with no item held.
_Avoid_: plain voice, normal voice

**Megaphone Voice**:
A player's voice heard through the in-game megaphone.
