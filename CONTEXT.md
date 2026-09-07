# CONTEXT — glossary & domain model

> **Authoritative for meaning.** When code, prose, or a plan disagrees with a definition here,
> **this file wins** — the term means what CONTEXT.md says it means, and the disagreeing thing is
> the bug. This is the *grill-with-docs* convention: plans are stress-tested against the language
> defined here, and **this file is updated inline** as decisions crystallize (don't let it lag
> the code).
>
> Keep entries short and cross-linked. A term defined here is the canonical name — use it
> verbatim in code, docs, and commits. ADRs (`docs/adr/`) record *decisions*; this file records
> *meaning*.

TravelEar is a Big Walk mod that lets the local player hear (and record) their own voice the way
other players in the session hear it.

## Glossary

### Voice

**Outbound Voice** — the local player's voice as the game transmits it to peers: mic audio after
the game's capture preprocessing and Opus encoding. Distinct from the raw mic feed in that it
already carries push-to-talk gating, noise suppression, and codec artifacts. (Avoid: mic feed,
raw mic, sidetone.)

**Local Voice** — the local player's **Outbound Voice** decoded and rendered through the game's
own voice playback processing (items, reverb, occlusion), as a peer would hear it. Distinct from
the game's own self-voice playback in that it is fed by decoded packets, not the preprocessed
mic. (Avoid: echo — a game audio bus name; sidetone; loopback; monitor.)

**Clean Voice** — a player's voice heard directly, with no item held. (Avoid: plain voice,
normal voice.)

**Megaphone Voice** — a player's voice heard through the in-game megaphone.

### Listening

**Ear** — the listener model used to render **Local Voice**. The **Self-Ear** is the local
player's own in-game listener at zero distance from the speaker, facing it, with no occlusion and
the player's own outdoorness. It is the only Ear in v1. Its angle is an open calibration point:
one's own voice reaches one's ears off-axis (a wide cone from just in front of the ears), so the
Self-Ear may sit part-way along the game's filter-angle curve rather than at angle 0 (see the
Self-Ear geometry note in `docs/DESIGN.md`). (Avoid: virtual listener, observer.)

### Processing

**Filter Stage** — the in-process per-voice DSP chain the game runs on each voice before it
reaches the mixer: decode, compression, soft clip, EQ, occlusion, item colouring. Captured
exactly by the **Tap**. (Avoid: filter chain, source effects.)

**Mixer Stage** — the effects the game applies inside Unity's audio mixer after the **Filter
Stage** (reverb sends, dry/high trims, megaphone character, compressors). Cannot be captured; the
mod re-synthesizes it (see [ADR-0002](docs/adr/0002-mixer-stage-resynthesis.md)). (Avoid:
post-processing, mixer effects.)

**Tap** — the point in the game's voice pipeline where **Local Voice** is captured for the
**Sink** instead of being mixed into game audio. The Tap always zeroes what it copies, so the
game never plays it. Since [ADR-0005](docs/adr/0005-sink-fed-from-the-encoder-thread.md) the
default feed point is the encoder thread, where nothing enters game audio at all; the Tap is the
`VoicePlayer` feed point's capture. (Avoid: hook, intercept.)

**Feed point** — where the **Sink** takes **Local Voice** from: the encoder thread (default,
mod DSP only) or the **Tap** behind an in-game `VoicePlayer` (config `Fidelity.SinkFeed`).
(Avoid: source, input path.)

### Output

**Sink** — the Windows-side audio stream that carries **Local Voice** out of the game so OBS can
record it as its own track. (Avoid: virtual cable, output device, audio source.)

**Helper** — the separate Windows process, outside the game's process tree, that renders the
**Sink** so OBS can capture it per-application (see
[ADR-0001](docs/adr/0001-helper-process-sink.md)). (Avoid: bridge, daemon, sidecar.)

**Offset** — the measured delay between the moment **Outbound Voice** leaves the mic and the
moment **Local Voice** reaches the **Sink**. (Avoid: latency, lag, sync.)
