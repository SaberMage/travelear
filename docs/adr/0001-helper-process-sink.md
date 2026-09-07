---
status: accepted
---

# The Sink is rendered by a separate Helper process, not a virtual audio cable

OBS's per-application audio capture keys on the process tree, so anything the mod renders inside the game process is inseparable from game audio. We chose to ship a small Helper process (launched outside the game's process tree) that renders the Sink via WASAPI to a user-chosen endpoint, so OBS can capture it as its own source with nothing extra installed. A virtual cable (VB-CABLE, VoiceMeeter) works for the author but requires every user to install and license a third-party driver, so it is documented as an alternative endpoint the same Helper can target, not the default.

## Considered options

- In-process WASAPI render to a virtual cable endpoint. Rejected as default: driver install and donationware licensing for every user; default cable latency ~150 ms.
- VBAN UDP emit. Rejected: the only OBS receiver plugin is unmaintained on Windows.
- Custom OBS plugin fed by a named pipe. Rejected: a third artifact to build and sign per OBS version.

## Consequences

- The Helper needs a visible top-level window (minimized is fine) titled "TravelEar for Big Walk" for OBS to enumerate it.
- Rendering to the system default device is audible; users who want a silent Sink pick an unused endpoint.
- Open verification: OBS process-loopback must capture streams rendered to a non-default endpoint. If it does not, the fallback is OBS Audio Input Capture on a cable endpoint, with no Helper code change.
