# docs-site

This project's developer docs. Authored in `src/*.md`, built with **mdBook**, published to
**GitHub Pages from this same repo**, and CI-gated against drift (see `docs/DOCS-STRATEGY.md`).

`theme/theme.css` is the **shared mdBook theme** — a Starlight-inspired skin reused verbatim
across consumer projects. Wire it via `additional-css = ["theme/theme.css"]` in `book.toml`;
re-point `--theme-accent` to rebrand.
