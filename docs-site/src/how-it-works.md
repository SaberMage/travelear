# How TravelEar hears you

Big Walk's voice chat is built on Dissonance with the Opus codec. When you speak, the game
preprocesses your mic (noise suppression, push-to-talk gating), encodes it with Opus, and sends
the packets to every other player. Each of their machines decodes your voice and runs it through
the game's playback processing before it reaches their speakers. That processing happens in two
places.

## The filter stage: captured exactly

The first place is a chain of filters on the voice's own audio source: the Opus decode, a
compressor and soft clipper, the per-player makeup gain that levels quiet microphones, an EQ
shaped by distance and facing angle, occlusion filtering when a wall sits between you, and the
megaphone's own colouring when you hold one.

TravelEar copies the exact packets the game sends, decodes them with the game's own decoder,
and runs them through the same processing the game applies for other players, ported step for
step from the game's code and checked against it: the compressor, soft clipper and makeup gain,
and the voice EQ. It is configured as if the listener stood at your own position: zero distance,
facing you, nothing in the way. All of this happens on the game's microphone thread, the same
thread that encodes your voice for other players, and the result goes straight to the Helper.
Nothing is played inside the game, so you never hear it through the game itself, and the game's
own audio hiccups cannot reach the recording any more than they reach other players.

## The mixer stage: re-synthesized

The second place is Unity's audio mixer. The game sets per-voice mixer parameters every frame:
how much of a voice goes to the valley or room reverb, dry and high-frequency trims, and the
megaphone's wet/dry blend and compression. Unity offers no way to read a single voice back out of
the mixer, so TravelEar cannot capture this part.

Instead it reads the same parameters the game writes and applies equivalent processing itself.
Gains, filters, and compressors match closely. Reverb is an approximation tuned by comparing
against recordings made on a real second client. Every re-synthesized effect can be switched off
individually with the `Fidelity` settings, so you can hear exactly which part is captured and
which is reproduced.

## What is deliberately left out

- **Walkie-talkie voice.** The game already plays your own voice through nearby paired
  walkie-talkies, so you hear it without TravelEar.
- **Radio.** The radio plays music, not player voice.
- **Distance and occlusion.** TravelEar renders from your own position. It does not try to
  guess how far away or behind which wall any particular listener is.

## The separate audio source

Everything the mod renders inside the game process would be captured by OBS together with the
rest of the game's audio, because OBS separates audio by process. TravelEar therefore sends the
processed voice to a small Helper process that plays it to a Windows playback device of your
choice. OBS captures the Helper with Application Audio Capture and gets its own track, with no
virtual audio cable driver required.
