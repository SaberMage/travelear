# CI model — agent-driven, autonomous, no-LLM-in-the-loop

> The default CI pattern for projects built from this skeleton: **deterministic gates** run by a
> **fleet runner-agent**, triggered by a git hook, reporting over **your messaging bus**. No LLM
> sits in the gate path — the gates are plain scripts; the "agent" is just the autonomous runner
> that fires them and reports. A hosted-runner workflow is an optional fallback.

## TravelEar specifics

- The plugin build needs Big Walk's Il2CppInterop proxy assemblies, which exist only on a
  machine with the game installed. The **build gate runs on the fleet host** that has the game
  (`GameDir` in `Directory.Build.props`), never on a hosted runner.
- The manual "run gates" command is `pwsh scripts/gates.ps1` (build → unit tests →
  `traceable-reqs check` → `mdbook build docs-site`).
- Hosted fallbacks exist for the game-independent gates: `.github/workflows/traceability.yml`
  (coverage gate) and `.github/workflows/docs-publish.yml` (docs build + GitHub Pages).

## The gates (deterministic)

Every gate is a script with a binary pass / fail — no judgment, no model:

1. **Build** — the project compiles / assembles clean.
2. **Unit tests** — the suite passes.
3. **`traceable-reqs check`** — requirement coverage gate; exit-1 fails the build (see
   `docs/TRACEABILITY.md`).
4. **Docs-drift** — generated docs (API reference, `llms.txt`, schema, CLI help) are regenerated
   and must match what's checked in; a diff fails (see `docs/DOCS-STRATEGY.md`).

Add project-specific deterministic gates here as needed (lint, schema validation, etc.). If your
project has acceptance tests that need a real environment, keep the *system under test* separate
from the *runner* — the runner orchestrates and asserts; it never *is* the thing being judged.

## Trigger: git hook → ping a runner-agent over the bus

The default trigger is **push-driven, not polling** (polling adds latency and wastes cycles):

1. A **git post-push hook** fires after a push.
2. The hook **pings a fleet runner-agent over your messaging bus** (a one-line "run gates for
   `<ref>`" message to the responsible runner).
3. The runner-agent **runs the deterministic gates** on the fleet.
4. The runner **reports the result back over the bus** to the responsible agent / channel.

This dogfoods the messaging bus as the project's own CI nervous system, and runs the gates on a
**real fleet host** — which matters when the acceptance bar needs a real environment a stock
hosted runner can't provide.

### Discovering the messaging-bus binary (robustly)

The bus binary's path may **change between versions** (it can live in a per-version plugins
folder or tool directory). The hook and runner **must locate it robustly** — resolve it at run
time (search the known install roots / a configured path / `PATH`) rather than hard-coding a
versioned location. A stale hard-coded path is the most likely cause of a silently dead trigger.

## Manual fallback

A **manual "run gates" command** must always exist — the same gate scripts, runnable by hand on
any fleet host. Use it when the hook didn't fire, when reproducing a failure, or before a hook is
wired at all.

## Hosted-runner workflow (optional)

A hosted CI workflow (e.g. GitHub Actions) is an **optional fallback**, useful for the pure
deterministic gates that need no special environment. `docs/TRACEABILITY.md` carries a ready
GitHub Actions snippet for the `traceable-reqs check` gate. Prefer the agent-driven fleet pattern
as the primary path when your acceptance tests need a real harness a hosted runner can't run.
