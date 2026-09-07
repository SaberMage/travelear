# Known Hazards

> A **conformance checklist, not advice.** Each hazard below is a first-class
> `REQ-HAZARD-*` requirement in `traceable-reqs.toml`, and is **not "covered" until a test tags
> it** (`unit`, plus `int` where the failure is cross-process / cross-node). This file exists to
> make "we won't re-break X" mechanical: an entry without a passing tagged test is an open risk,
> and `traceable-reqs check` will say so once the hazard is activated.

A hazard earns a place here when it is an invariant you have *paid for once* (a real bug, an
incident) or one you have *committed never to introduce*. State it so a test can prove it.

## Entry format

Each entry is one numbered subsection with these fields:

- **Failure** — the concrete bad behavior: what goes wrong, under what sequence / timing / input.
- **Invariant** — the property that MUST hold, phrased so a test can assert it (the thing the
  `REQ-HAZARD-*` requires).
- **Mapping / notes** — where this lives in *this* project, and anything that changes the shape
  of the test.
- **cite** — where the failure / fix is evidenced; reference only — the binding evidence is the
  tagged test.

---

## 1. Audio isolation

### 1.1 Local Voice leaks into the game mix

- **Failure:** the mod-owned renderer's buffer reaches Unity's mixer, so the local player hears
  themselves with a delay and OBS's game track contains their voice twice.
- **Invariant:** after the Tap runs, every sample the renderer hands back to Unity is zero, for
  every channel count and buffer size the game uses.
- **Mapping / notes:** the Tap is the last `IAudioFilter` on the renderer's AudioSource. Unit
  test drives the filter with a non-zero buffer and asserts the buffer is all zeros afterwards
  and the ring buffer holds the original samples.
- **cite:** design decision, `docs/DESIGN.md` (Local Voice renderer). `REQ-HAZARD-NO-GAME-AUDIO-LEAK`.

## 2. Gameplay independence

### 2.1 Sink failure changes gameplay

- **Failure:** the Helper is absent, OBS is not running, the pipe breaks mid-session, or the
  Helper crashes, and the mod throws on the audio thread, blocks a frame, respawns in a loop, or
  mutes the game's own voice playback.
- **Invariant:** every Sink-side failure is absorbed: the renderer keeps producing, the pipe
  writer drops frames, the mod logs once and retries the connection every 5 s, and nothing on
  the game's audio path is touched.
- **Mapping / notes:** pipe writer and Helper spawner. Unit tests simulate a closed pipe and a
  failed spawn and assert no exception escapes and the retry cadence holds.
- **cite:** grilling session decision Q16. `REQ-HAZARD-NO-GAMEPLAY-IMPACT`.

## 3. Fidelity honesty

### 3.1 Silent degradation after a game update

- **Failure:** a game update renames a class, method, field, or mixer parameter; a Harmony
  patch or `AudioMixer.GetFloat` fails; the mod keeps streaming Filter-Stage-only or
  raw-mic audio that no longer represents how others hear the player.
- **Invariant:** every hook and every mixer parameter is resolved at startup; if any is missing
  the mod sets itself disabled, logs one clear line naming the missing symbol, and the Sink
  emits silence.
- **Mapping / notes:** a single bind step that collects all symbols before any patch is applied.
  Unit test feeds a symbol table with one entry missing and asserts the disabled state and the
  single log line.
- **cite:** grilling session decision Q19; `docs/adr/0002-mixer-stage-resynthesis.md`.
  `REQ-HAZARD-NO-PARTIAL-FIDELITY`.

## 4. Network surface

### 4.1 The mod becomes visible to peers

- **Failure:** the Local Voice renderer is implemented as a synthetic remote player or sends
  any Mirror or Dissonance message, so peers see a phantom player, hear duplicated voice, or
  the host rejects the client.
- **Invariant:** the mod only reads outbound packets; it never calls a send method, never
  registers a network identity, and never joins a Dissonance room.
- **Mapping / notes:** the synthetic-remote-player design is a documented fallback only (ADR
  0002). Unit test asserts the Harmony patch set contains only postfixes on send methods and no
  prefixes that alter arguments or skip the original.
- **cite:** grilling session decision Q22. `REQ-HAZARD-NO-PEER-SURFACE`.
