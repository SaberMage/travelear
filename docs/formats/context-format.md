# How to write & maintain CONTEXT.md

> `CONTEXT.md` is the project's glossary and domain model, and it is **authoritative for
> meaning**: when code or prose disagrees with a definition, the definition wins and the code is
> the bug. The *grill-with-docs* loop reads it to sharpen terminology and **updates it inline**
> as decisions land — it must not lag the code.

## What belongs here (and what doesn't)

- **Here:** the *meaning* of a term — what it is, why it exists, the invariants baked into the
  word, how it differs from neighbouring terms.
- **Not here:** *decisions* (those are ADRs in `docs/adr/`) and *requirements* (those are
  `REQ-*` ids in `traceable-reqs.toml`). CONTEXT defines the vocabulary the ADRs and reqs are
  written in.

## Entry conventions

- **Canonical name.** The term as written here is *the* name — use it verbatim in code, docs,
  commits, and plans. If two names exist, pick one, define it, and note the other as "(formerly
  X)".
- **Format:** `**Term** — definition in one or two sentences.` Lead with what it *is*, then any
  constraint that is part of its meaning.
- **Cross-link** related terms in `**bold**`, and draw the **boundary** between terms that are
  easy to conflate ("distinct from **X** in that …").
- **Short.** A glossary entry is a definition, not an essay. If it needs a rationale, that
  rationale is probably an ADR — link to it.

## Maintenance discipline

- When a grilling session or an ADR changes what a term *means*, edit the entry **in the same
  change** — a stale definition is worse than none, because this file is authoritative.
- New domain term introduced in a plan or PR → add it here before (or with) the code that uses
  it, so reviewers and dev-agents share one vocabulary.
- If an entry is the `doc`-stage evidence for a requirement, tag it in place:
  `<!-- [<doc>->REQ-…] -->` — drop the angle brackets around the stage when using it for
  real (see `docs/TRACEABILITY.md`). `CONTEXT.md` is a valid `[scan].root`.
