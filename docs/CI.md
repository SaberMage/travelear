# CI model — manual, deterministic, no hosted runners

> TravelEar deliberately runs **no hosted CI**: no GitHub Actions, no external runners. The
> project is small-scope, and the plugin build needs Big Walk's Il2CppInterop proxy assemblies,
> which exist only on a machine with the game installed. The gates are plain scripts run by hand
> (or by a live agent) on a dev host before every release and before any work is declared done.

## The gates (deterministic)

Every gate is a script with a binary pass / fail — no judgment, no model:

1. **Build** — `dotnet build TravelEar.sln -c Release` compiles the plugin against the game's
   proxy assemblies and the Helper.
2. **Unit tests** — `dotnet test` on `tests/TravelEar.Tests` (xunit, covers the pure
   `TravelEar.Core` library).
3. **`traceable-reqs check`** — requirement coverage gate; exit-1 fails (see
   `docs/TRACEABILITY.md`).
4. **Docs build** — `mdbook build docs-site` must succeed; the site is read in-repo and built
   locally, never published by a hosted job.

## Running the gates

```
pwsh scripts/gates.ps1            # all gates
pwsh scripts/gates.ps1 -SkipDocs  # while iterating on code
```

Run them:

- before declaring any body of work done (binding rule in `AGENTS.md`);
- before every release tag (`docs/RELEASE-RUNBOOK.md`).

The host needs: .NET 8 SDK, Big Walk with BepInEx 6 launched at least once (so
`BepInEx/interop/` exists), `traceable-reqs` on `PATH`, and `mdbook`.

## What is deliberately not here

- No `.github/workflows/`. Adding one is a scope decision for the operator, not a default.
- No GitHub Pages. `docs-site/` is the documentation source of truth and is browsable on
  GitHub as markdown; `mdbook build` is a local check that it stays well-formed.
- No git-hook trigger or fleet runner-agent. If the project grows, the pattern to adopt is a
  post-push hook that pings a runner-agent over the messaging bus to run `scripts/gates.ps1`
  and report back; until then, the manual command is the whole CI.
