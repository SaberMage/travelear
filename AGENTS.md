# TravelEar — agent working rules

> **Canonical source of truth for how agents work in this repo.** Harness-agnostic:
> Claude Code reads it via the one-line `CLAUDE.md` (`@AGENTS.md`); other harnesses read
> this file directly. Edit working rules HERE, never in `CLAUDE.md`.

`TravelEar` is `a Big Walk mod (BepInEx 6, IL2CPP) that renders the local player's own voice
the way other players hear it and streams it to a separate Windows audio source for OBS`.

**Orientation — read before working:** `README.md` (user-facing surface), `docs/DESIGN.md`
(pipeline, components, scope ladder, spikes), `CONTEXT.md` (glossary/model, authoritative for
meaning), `docs/adr/` (decisions), `docs/KNOWN-HAZARDS.md` (invariants we must not re-break),
`docs/{DOCS-STRATEGY,TRACEABILITY,RELEASE-RUNBOOK,CI}.md`.

## Requirement traceability (binding)

This project uses [`traceable-reqs`](https://github.com/BigscreenVR/traceable-reqs)
(`traceable-reqs.toml` = the authoritative `REQ-*` registry). The full contract is
`docs/TRACEABILITY.md`. The rules you must follow:

1. **Tag evidence in the same change.** When you write a function/test/doc-section that
   satisfies a requirement stage, add its tag in that same commit:
   `// [impl->REQ-FOO]` · `// [unit->REQ-FOO]` · `<!-- [doc->REQ-FOO] -->`
   Stages: `doc` / `impl` / `unit` / `int`. Tag *on or immediately above* the real
   evidence — never at file tops to satisfy coverage.
2. **Run `traceable-reqs check` before declaring work done.** Exit-1 means missing/invalid
   evidence — fix it, don't ship it.
3. **New requirement → add it to `traceable-reqs.toml` first** (with a `REQ-*` id), then
   satisfy it. No untracked work; no untagged evidence.
4. **KNOWN-HAZARDS are `REQ-HAZARD-*` requirements** — each needs a test before it's
   "covered." Treat the hazard list as a conformance checklist you must satisfy, not advice.
5. **Activate, don't pre-fail.** Requirements you aren't yet working stay
   `required_stages = []`. Activate (set real stages) only when starting the milestone that
   delivers them.

## Other conventions

- Match surrounding code style. C# only; `Nullable` is disabled in the plugin project because
  the Il2CppInterop proxy assemblies clash with the compiler-synthesized nullable attributes.
- **Dissonance's network protocol is copied verbatim.** The Outbound Voice tap parses the
  VoiceData frame exactly as documented at
  https://placeholder-software.co.uk/dissonance/docs/Reference/Networking/Network-Protocol.html
  (magic `0x8BC7`, type, session id, sender id, flags, sequence, channel list, Opus payload).
  Everything else is clean-room against the game's decompiled signatures.
- **Game symbols are facts, not guesses.** Class, method, field, and mixer-parameter names come
  from the decompiled game (`BepInEx/interop/*.dll` signatures + Cpp2IL bodies). When a game
  update renames one, the mod hard-fails and logs; it never silently degrades to partial
  fidelity (see `docs/KNOWN-HAZARDS.md`).
- **Never read mixer floats through `AudioMixer.GetFloat`.** It is an unstripped Unity 6 body
  that throws `MissingMethodException` in this game, and its native icall is not registered.
  Read exposed floats from the game's own `SetFloat` writes (`MixerFloats`, a Harmony postfix);
  details in `docs/reference/big-walk-environment-reverb.md` section 6.
- Honor every `docs/KNOWN-HAZARDS.md` invariant — each is a `REQ-HAZARD-*` with a test.
- Docs are dual-audience (human + AI dev-agent) per `docs/DOCS-STRATEGY.md`; the docs-site
  build is a CI gate.
- **Local build needs the game.** The plugin references proxy assemblies under the Big Walk
  install (`GameDir` in `Directory.Build.props`, override in `Directory.Build.props.user`).
- **No hosted CI.** No GitHub Actions or runners; `pwsh scripts/gates.ps1` is the whole CI and
  runs by hand before work is declared done and before every release (`docs/CI.md`).
- Commit messages end with the co-author trailer of the agent that wrote them. Live agent
  commits add `Co-authored by: lia`; harness trailers (e.g. `Co-Authored-By: Claude …`) are kept
  as well.

## Plans and context hygiene

- **JIT plans.** Plan the next immediate body of work just-in-time (a short `*-PLAN.md`),
  not the whole project up front. A plan names scope, open design questions, tasks, and the
  gate (build + `traceable-reqs check` green). `docs/DESIGN.md` is the standing design; plans
  are per-milestone.
- If you finish a significant body of work without need for user intervention, or if your
  context gets too high, you can clear your own context and keep moving:
  1. Create a JIT plan for the next immediate body of work, if it isn't already planned.
  2. If you are a live agent: commune immediate next steps + a broad project-status/end-goal
     summary, then clear.
