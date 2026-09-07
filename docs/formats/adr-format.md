# How to write an ADR

> ADRs (`docs/adr/`) record **decisions** — what we chose and why — so they aren't
> re-litigated. They are the companion to `CONTEXT.md` (which records **meaning**). The
> *grill-with-docs* loop writes and supersedes ADRs inline as decisions crystallize. Start from
> `docs/adr/0000-template.md`.

## Conventions

- **One file per decision**, numbered sequentially: `0001-<slug>.md`, `0002-<slug>.md`, …
  Never renumber a published ADR — it's a stable anchor.
- **Title = the decision as its outcome**, not the question. "Sessions are pinned at first
  bind" — not "How should sessions bind?". A reader skimming filenames should see what was
  decided.
- **Four sections, always, in order:** `## Status`, `## Context`, `## Decision`,
  `## Consequences`. Add an optional `## Alternatives considered` when the rejected options carry
  real weight.

## The sections

- **Status** — `proposed` | `accepted` | `superseded`, plus a date. If it changes an earlier
  decision, link it: `supersedes [ADR-NNNN](NNNN-….md)`. When a later ADR overrides this one,
  come back and mark this one `superseded by …`. Be precise about *what* is superseded — a later
  ADR may reverse one mechanism while keeping another's framing.
- **Context** — the forces that made the decision necessary: constraints, the real problems
  (number them if several), what a reader who wasn't in the room needs to understand the *why*.
  Refer to terms by their canonical `CONTEXT.md` names.
- **Decision** — what was decided, stated plainly and **actively**. Include the specifics of what
  gets built / changed, and name the tempting alternative you rejected so it stays rejected.
- **Consequences** — what becomes true now, good *and* bad: breaking changes, new obligations
  (e.g. "new `REQ-*` ids to mint", "the integration checklist must be updated"), and what this
  forecloses or forces later.

## Tagging

If an ADR section is the `doc`-stage evidence for a requirement, tag it in place:
`<!-- [<doc>->REQ-…] -->` (drop the angle brackets around the stage when using it for
real) immediately above that section (see `docs/TRACEABILITY.md`).
