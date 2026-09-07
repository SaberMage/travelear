# The Sink is rendered by a separate Helper process, not a virtual audio cable

## Status

accepted (2026-09-06)

## Context

1. OBS's Application Audio Capture keys on the process tree, so anything the mod renders inside
   the game process is inseparable from game audio.
2. Separation must therefore come from a distinct Windows endpoint or a distinct process.
3. A virtual cable (VB-CABLE, VoiceMeeter) gives a distinct endpoint and already exists on the
   author's machine, but every other user would have to install and license a third-party
   kernel driver, and VB-CABLE's default buffer adds ~150 ms.
4. VBAN emit has no maintained OBS receiver on Windows; a custom OBS plugin is a third artifact
   to build and sign per OBS version.

## Decision

Ship a small **Helper** process, launched outside the game's process tree, that reads
**Local Voice** from the mod over a named pipe and renders it via WASAPI to a user-chosen
endpoint. OBS captures the Helper with Application Audio Capture, so users install nothing
extra. A virtual cable remains a supported *endpoint* the same Helper can target, documented for
users who want a silent Sink, never the default.

Rejected: in-process WASAPI render to a cable endpoint as the default path.

## Consequences

- The Helper needs a visible top-level window (minimized is fine) titled
  "TravelEar for Big Walk" so OBS can enumerate it.
- Rendering to the system default device is audible; users who want silence pick an unused
  endpoint via `SinkEndpoint`.
- Open verification: OBS process-loopback must capture streams rendered to a non-default
  endpoint. If it does not, the fallback is OBS Audio Input Capture on a cable endpoint, with no
  Helper code change.
- The Helper is a second deliverable (self-contained .NET 8 WinExe) with its own lifecycle
  requirements (`REQ-SINK-*`).
