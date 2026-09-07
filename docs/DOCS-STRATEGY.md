# Documentation strategy

> A **designed-in commitment**, not an afterthought — grounded in research on
> critically-acclaimed developer docs (Stripe, Twilio, the Rust Book, FastAPI, Cloudflare,
> Anthropic, Diátaxis, `llms.txt`, the Google developer style guide). Governs the **shipped
> product docs** (authored as code lands). The planning docs (CONTEXT / ADRs / design docs) are
> internal and separate.
>
> **Where the docs live:** docs live in **this same repo** under `docs-site/` and are read there
> as markdown. `mdbook build docs-site` runs in the manual gates (`scripts/gates.ps1`) as a
> well-formedness check; nothing is published to GitHub Pages (`docs/CI.md`). Where this file
> says "CI", read "the manual gates run before each release".

## The defining constraint: a dual audience

These docs serve **two readers at once** — human developers *and* the **AI dev-agents that
build on or integrate with this project**. This is the design constraint: every artifact is
authored **once in clean markdown** and served in **two depths** (human-rendered + agent-export).
The agent layer is **first-class, not optional** — make it so good that a dev-agent integrates
correctly on the first try.

## Principles (top techniques, prioritized)

1. **Sub-10-minute killer quickstart** — runnable, deterministic, whole-working-thing-first, no
   placeholders. Time-to-first-hello-world is the single most-cited conversion lever.
2. **Diátaxis four-mode separation** — tutorial / how-to / reference / explanation, never mixed
   (mixing is the most-cited cause of confusing docs).
3. **Deterministic, real, copy-pasteable examples everywhere** — no `<YOUR_VALUE_HERE>`
   placeholders; real values that run. Serves humans *and* agents.
4. **Dual-depth agent exports** — `llms.txt` (slim curated index) + `llms-full.txt` (full
   concatenated export), auto-generated in CI, plus markdown content negotiation (a `.md` suffix
   alongside each `.html`, or `Accept: text/markdown`) which cuts agent token use ~90% vs HTML.
5. **One canonical way to do X** — explicitly mark deprecated / alternate paths. Non-determinism
   is fatal for agents.
6. **Complete reference, auto-generated, all error variants** — generate API reference from the
   code for your public surface, plus any machine-readable contract/schema. Generic placeholders
   in reference are a failure mode.
7. **Consistent, positive, conversational voice** — adopt the Google developer style guide:
   second person ("you"), active voice, knowledgeable-friend tone. **Frame guidance positively
   and prescriptively** — state what the reader must *do*, not what they must *not* do ("always
   cut on a record boundary", not "never cut mid-record"; "`add` activates your adapter", not
   "the binary is not an active adapter"; "X is exclusive to Y", not "X must not have Y"). Flip
   prohibitions into prescriptions; lead with the must-do and keep the *why* when it earns its
   place. Reserve descriptive negatives for genuine system facts where the positive form would
   lose precision (e.g. "never spooled" as a contract term).
8. **Explain *why*, not just *what*** — conceptual docs + diagrams for the project's core model
   and state machines.
9. **Stable, never-renamed anchors / URLs** — agents cache links.
10. **Docs-as-product, gated in CI** — generation (API reference, `llms.txt`, schema, CLI help)
    is part of the build so docs can never drift from code. Drift is the #1 most-cited docs
    failure; this kills it structurally.

## Information architecture — by capability vertical

Organize by capability, each vertical carrying the same four Diátaxis modes internally (the
Cloudflare pattern):

**TravelEar's verticals: Install & OBS setup · Settings · Fidelity (what is captured, what is
re-synthesized) · Helper & Sink (endpoints, monitoring, Offset).** The site lives in
`docs-site/src/`; `SUMMARY.md` is the map.

Global sequence (Rust Book logic): early runnable project → dependency-ordered concepts →
capstone last. Per-vertical internal template (Django labels × Cloudflare ordering):
`Overview (why + diagram) · Quickstart/Tutorial · How-to guides · Reference (generated) ·
llms.txt`.

## Killer quickstart targets

One per audience: the **player** quickstart (`docs-site/src/install.md`: fresh BepInEx install
to a separate OBS track in under 10 minutes, whole-thing-first then decomposed) and the
**dev-agent** quickstart (`AGENTS.md` + `docs/DESIGN.md`: build the plugin against the game's
proxy assemblies and run `scripts/gates.ps1`). TravelEar has no integration surface for other
mods; the named pipe between mod and Helper is internal. Zero placeholders; every value runs.

## Agent-consumable docs

- **`llms.txt` / `llms-full.txt`** — auto-emitted in CI; the slim index answers quick questions,
  the full export feeds deep ingestion. Two-level (Cloudflare pattern): a curated root index that
  fans out to per-vertical `llms.txt`; `llms-full.txt` is CI-concatenated page bodies
  (generation-only, never hand-authored).
- **Markdown content negotiation** + deterministic include/exclude tags so agent exports carry
  the canonical path and drop human-only narrative.
- **Machine-readable contract/schema** (e.g. JSON Schema) at a stable, discoverable path, if your
  project has one — the schema *is* documentation.
- **Config keys as first-class agent docs** — TravelEar ships no CLI; its user surface is the
  BepInEx config file. Every key's description string in `Plugin.cs` is the canonical one-line
  doc, and `docs-site/src/settings.md` mirrors it (drift-checked in CI).

## Site generator: mdBook + custom theme CSS

**mdBook** is the generator. The **shared theme** lives in `docs-site/theme/` (a
Starlight-inspired skin reused across consumer projects — re-point the accent to rebrand);
**Astro Starlight is the styling north star** (copy its look / feel in the theme CSS, not its
toolchain). Raw `.md` is published alongside each rendered page (`/x.html` ↔ `/x.md`) for the
agent-export convention; `llms.txt` / `llms-full.txt` / any schema are static assets at site root.

## CI commitments

Generated reference, the schema, `llms.txt` / `llms-full.txt`, and CLI help exports are
**generated and checked in CI** — a doc-drift gate. The mdBook build + GitHub Pages publish run
in the same pipeline. Doc quality lives on the same footing as tests.

## Anti-patterns to design against (most-cited failures)

Doc / code drift (#1 — solved by CI gating); *what* without *why*; too much setup before first
success; generic placeholders in reference; mixed Diátaxis modes; **guidance framed as
prohibitions ("don't" / "never do X") instead of positive prescriptions**; poor search /
navigation; multiple non-canonical ways to do X (fatal for agents).
