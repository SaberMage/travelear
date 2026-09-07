# Release runbook

> How a TravelEar release ships. Everything is manual and runs on a dev host with the game
> installed; there is no hosted CI (`docs/CI.md`). The spine: gates · changelog · bump · tag ·
> build artifacts · GitHub Release. TravelEar does not sign artifacts. Thunderstore packaging is a
> later, separate step.

## One-time setup

- A dev host with .NET 8 SDK, Big Walk + BepInEx 6 (launched once so `BepInEx/interop/`
  exists), `traceable-reqs`, `mdbook`, and `gh` authenticated as `SaberMage`.
- No release keys and no repository secrets.

## Per release

1. **Run the gates**: `pwsh scripts/gates.ps1`. Everything green, working tree clean.
2. **Bump** the version in `Directory.Build.props` (`<Version>`) and in
   `src/TravelEar/Plugin.cs` (`VersionString`, which BepInEx reports). Both carry the same
   number.
3. **Write the user-facing changelog** — add a `## [X.Y.Z] - YYYY-MM-DD` section at the top of
   `CHANGELOG.md` with **Added / Changed / Fixed** subsections. This section is pasted verbatim
   as the GitHub Release body, so it is the changelog every user reads. Rules:
   - **User-facing UX only.** What a person using the mod notices or does differently. Name
     the actual config keys, OBS steps, or in-game surfaces they touch.
   - **No internal lingo** — no requirement ids, internal class names, commit hashes, or
     milestone / hazard codes. A reader who has never seen the source must understand every line.
   - **Flag breaking changes** prominently under Changed (a renamed config key, a new minimum
     BepInEx build).
   - A tag with no matching `## [X.Y.Z]` section is a mistake; stop and write one.
4. **Commit and tag**: commit the bump + changelog, then
   `git tag vX.Y.Z && git push origin main vX.Y.Z`.
5. **Build the artifacts** on the dev host:
   ```
   dotnet build src/TravelEar -c Release
   dotnet publish src/TravelEar.Helper -c Release
   ```
   Assemble `dist/TravelEar/` containing `TravelEar.dll` and `TravelEar.Helper.exe`, zip it as
   `dist/TravelEar-vX.Y.Z.zip`, and write `dist/SHA256SUMS.txt`.
6. **Create the GitHub Release** on this repo with the changelog section as the body:
   ```
   gh release create vX.Y.Z dist/TravelEar-vX.Y.Z.zip dist/SHA256SUMS.txt \
     --title "TravelEar vX.Y.Z" --notes-file <extracted changelog section>
   ```
   No signing; the published release is the final artifact.
7. **Docs**: nothing to publish. `docs-site/` ships in the repo and was verified by the gates.
   Thunderstore upload (manifest.json + icon + README) is a manual follow-up once v1 is stable.

## Notes

- **Two independent numbers — never conflate them:** the mod **semver** (`vX.Y.Z`, one number
  in both `Directory.Build.props` and `Plugin.cs`) and the **BepInEx build** the mod is pinned to
  (`6.0.0-be.755` in `TravelEar.csproj`). A BepInEx bump is a Changed entry, not a version
  scheme.
- There is no wire protocol of our own. The named-pipe frame between mod and Helper is
  internal, and both halves ship in the same zip, so it changes freely between releases.
