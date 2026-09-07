# Release runbook

> How a TravelEar release ships. The spine is fixed (changelog · bump · regenerate docs · tag ·
> GitHub Release · docs publish). TravelEar does not sign artifacts; the GitHub Release is the
> final artifact, and Thunderstore packaging is a later, separate step.

## One-time setup

- GitHub Pages for `SaberMage/travelear` is served from the `docs-publish` workflow (source:
  GitHub Actions). Enable it once under repository Settings → Pages.
- No release keys. The release workflow uses the repository's default `GITHUB_TOKEN`.
- A fleet host with Big Walk installed runs the build gate (`scripts/gates.ps1`), because the
  plugin references the game's proxy assemblies (`docs/CI.md`).

## Per release

1. **Bump** the version in `Directory.Build.props` (`<Version>`) and in
   `src/TravelEar/Plugin.cs` (`VersionString`, which BepInEx reports); **regenerate generated
   docs** (`mdbook build docs-site`); run `scripts/gates.ps1` and land everything green.
2. **Write the user-facing changelog** — add a `## [X.Y.Z] - YYYY-MM-DD` section at the top of
   `CHANGELOG.md` with **Added / Changed / Fixed** subsections. This section becomes the GitHub
   Release **body** verbatim, so it is the changelog every user reads. Rules:
   - **User-facing UX only.** What a person using the mod notices or does differently. Name
     the actual config keys, OBS steps, or in-game surfaces they touch.
   - **No internal lingo** — no requirement ids, internal class names, commit hashes, or
     milestone / hazard codes. A reader who has never seen the source must understand every line.
   - **Flag breaking changes** prominently under Changed (for example a renamed config key or a
     new minimum BepInEx build).
   - The release **fails loudly** if the tagged version has no matching `## [X.Y.Z]` section.
3. **Tag**: `git tag vX.Y.Z && git push origin vX.Y.Z`. This triggers:
   - the **release workflow** — build the plugin on the fleet host, publish the Helper
     self-contained, zip `TravelEar/TravelEar.dll` + `TravelEar/TravelEar.Helper.exe` as
     `TravelEar-vX.Y.Z.zip`, extract this version's `CHANGELOG.md` section into the release body,
     and create the **GitHub Release on this same repo** with the zip + SHA-256 checksums;
   - the **docs-publish workflow** — build `docs-site/` and publish to GitHub Pages (drift-gated;
     see `docs/DOCS-STRATEGY.md`).
4. **Publish**: no signing. The workflow's draft release is the final artifact; publish it.
   Thunderstore upload (manifest.json + icon + README) is a manual follow-up once the mod has a
   stable v1.

## Notes

- **Two independent numbers — never conflate them:** the mod **semver** (`vX.Y.Z`, one number
  used in both `Directory.Build.props` and `Plugin.cs`) and the **BepInEx build** the mod is
  pinned to (`6.0.0-be.755` in `TravelEar.csproj`). A BepInEx bump is a Changed entry, not a
  version scheme.
- There is no wire protocol of our own. The named-pipe frame between mod and Helper is
  internal, and both halves ship in the same zip, so it changes freely between releases.
