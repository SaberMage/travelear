# Traceability development contract

> How this project uses [`traceable-reqs`](https://github.com/BigscreenVR/traceable-reqs)
> to keep every requirement traced from doc → impl → test. The manifest is
> `traceable-reqs.toml` (seed it from your requirements source + your KNOWN-HAZARDS
> invariants). This contract makes the trace pay off instead of rotting.

## Why

A requirement that isn't traced to real evidence (a doc, an implementation, a test) is a
requirement you're only *hoping* is met. This contract converts that hope into a build that
**fails when evidence is missing** — so scope ships verifiably and known hazards can't
silently regress.

## The contract (rules)

1. **The manifest is the authoritative requirement registry.** Every requirement and every
   KNOWN-HAZARDS invariant exists in `traceable-reqs.toml` as a `REQ-*` id. No work without a
   REQ; no REQ without intent to satisfy. Your prose source (PRD / spec / design doc) holds the
   wording; the manifest holds the id + `required_stages`.
2. **Tag in the same change as the evidence.** When you write the function / test / doc-section
   that satisfies a stage, add its `[<stage>-><REQ-ID>]` tag in *that same commit*. Never "tag
   later" — this single rule is what stops the trace from drifting.
3. **Evidence-proximate tags, not file-tops.** A tag sits on or immediately above the real
   function, test, doc section, config entry, or workflow step that proves the stage. One tag =
   one piece of real evidence. Tags at file tops to satisfy coverage are noise and usually wrong.
4. **KNOWN-HAZARDS are first-class requirements.** Each invariant is a `REQ-HAZARD-*` requiring
   `unit` (and `int` where cross-process / cross-node). A hazard cannot be "covered" without a
   test tagging it — the anti-regression promise becomes mechanical.
5. **Activation, not premature failure.** Every requirement starts `required_stages = []`
   (inactive) so `check` stays green pre-code. A **milestone activates** the requirements it
   delivers by setting their real `required_stages`. Deferred items stay `[]` until promoted.
6. **Scan roots stay honest.** `[scan].roots` includes every evidence location (your source,
   tests, docs, and later your CI workflows and scripts). Audit roots whenever a new evidence
   dir appears — a missing root makes evidence *silently* vanish from the trace.

### Stages

`doc` (prose / design / API-doc) · `impl` (production code) · `unit` (unit test) · `int`
(integration / e2e / cross-process / cross-node). Default activation policy:
`["doc","impl","unit"]`; networking / lifecycle / cross-node reqs add `int`.

### Tag examples

Real tags are `[<stage>->REQ-ID]` with a bare stage word. The examples below angle-bracket the
stage (`[<impl>->...]`) only so the scanner treats them as illustrations, not live evidence —
write a bare stage word in real code.

```rust
// [<impl>->REQ-EXAMPLE-1]
fn do_the_thing(...) { ... }
```
```rust
// [<unit>->REQ-HAZARD-EXAMPLE]
#[test] fn upholds_the_invariant() { ... }
```
```markdown
<!-- [<doc>->REQ-EXAMPLE-1] -->
## The thing, explained
```

## Enforcement (four layers — defense in depth)

1. **CI gate (hard backstop).** `traceable-reqs check --json` runs on every PR / push; exit-1
   (`missing_stage` / `undeclared_id` / `parse_error` / `manifest_error`) **fails the build**. A
   PR can't merge if it leaves an *activated* req's required stage uncovered. Runs alongside the
   DOCS-STRATEGY docs-drift gate.
2. **Milestone activation gate.** Starting a milestone flips its reqs from `[]` to the real
   policy; the milestone **is not done until `check` is green for its reqs**. Coverage grows
   *with* the project instead of failing on day one.
3. **Agent-facing rule.** The repo `AGENTS.md` instructs every dev-agent: tag
   `[<stage>-><REQ-ID>]` in the same change as the evidence, and run `traceable-reqs check`
   before declaring done. If this project is built *with* agents, the contract must be
   machine-followable — agent discipline + CI as the net.
4. **Quality audit (anti-box-ticking).** `check` proves a tag *exists*; `traceable-reqs review`
   + `lint` audit whether tags sit near *real* evidence and whether titles are meaningful. Run
   advisory in CI (or periodically). Presence = hard gate; quality = audited.

Optional fast-feedback: a pre-push git hook running `traceable-reqs check` locally.

## CI snippet (hosted-runner fallback; see `docs/CI.md` for the agent-driven default)

```yaml
# .github/workflows/traceability.yml
name: traceability
on: [push, pull_request]
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Install traceable-reqs
        run: gh release download --repo BigscreenVR/traceable-reqs --pattern '*linux-x86_64' --output traceable-reqs && chmod +x traceable-reqs
        env: { GH_TOKEN: ${{ github.token }} }
      - name: Coverage gate (hard)
        run: ./traceable-reqs check --json
      - name: Quality audit (advisory)
        run: ./traceable-reqs lint || true
```

## Lifecycle

- **Now (planning):** manifest seeded, all reqs inactive. `check` is green (nothing required
  yet).
- **First milestone:** install the CLI, validate the seed against it, wire the CI gate,
  **activate that milestone's reqs** (and the `REQ-HAZARD-*` invariants it must uphold), tag as
  code lands.
- **Each later milestone:** activates + covers its requirements before it's called done.
