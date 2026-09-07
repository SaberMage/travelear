# docs-site

This project's user docs. Authored in `src/*.md` and read in-repo; `mdbook build docs-site` runs
in the manual gates as a well-formedness check (see `docs/DOCS-STRATEGY.md`, `docs/CI.md`).

`theme/theme.css` is the **shared mdBook theme** — a Starlight-inspired skin reused verbatim
across consumer projects. Wire it via `additional-css = ["theme/theme.css"]` in `book.toml`;
re-point `--theme-accent` to rebrand.
