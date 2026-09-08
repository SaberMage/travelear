# Assembles the release artifacts for the version in Directory.Build.props (docs/RELEASE-RUNBOOK.md,
# step 5). Run after the gates are green and the version + changelog are committed and tagged.
# Usage: pwsh scripts/release.ps1 [-SkipBuild] [-Draft]
#   -Draft   smoke-test the assembly before the version is cut: takes the notes from the
#            [Unreleased] section instead of requiring a ## [X.Y.Z] one.
#
# Output (git-ignored):
#   dist/TravelEar-vX.Y.Z/BepInEx/plugins/TravelEar/TravelEar.dll
#   dist/TravelEar-vX.Y.Z/BepInEx/plugins/TravelEar/TravelEar.Core.dll
#   dist/TravelEar-vX.Y.Z/BepInEx/TravelEar.Helper/TravelEar.Helper.exe   (single-file, self-contained)
#   dist/TravelEar-vX.Y.Z.zip                                              (extract into the Big Walk folder)
#   dist/SHA256SUMS.txt
#   dist/RELEASE-NOTES-vX.Y.Z.md                                           (the changelog section, for gh release)
#
# The Helper lives OUTSIDE BepInEx\plugins on purpose: BepInEx examines every DLL under plugins as a
# plugin candidate, and the plugin's default Sink.HelperPath is BepInEx\TravelEar.Helper\TravelEar.Helper.exe.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# Version of truth: Directory.Build.props; Plugin.cs must carry the same number.
$props = [xml](Get-Content 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0].Trim()
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
$pluginVersion = (Select-String -Path 'src/TravelEar/Plugin.cs' -Pattern 'VersionString\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if ($pluginVersion -ne $version) { throw "Version mismatch: Directory.Build.props $version, Plugin.cs $pluginVersion." }

# The changelog section for this version, pasted verbatim as the GitHub Release body.
$changelog = Get-Content 'CHANGELOG.md' -Raw
$section = if ($Draft) { 'Unreleased' } else { $version }
$m = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($section))\][^\n]*\n(.*?)(?=^## \[|\z)")
if (-not $m.Success) { throw "CHANGELOG.md has no '## [$section]' section. Write one before releasing." }
if ($Draft) { Write-Host '==> DRAFT: notes taken from [Unreleased]; not for publishing' }
$notes = $m.Groups[1].Value.Trim()

$name = "TravelEar-v$version"
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist $name
$pluginDir = Join-Path $stage 'BepInEx/plugins/TravelEar'
$helperDir = Join-Path $stage 'BepInEx/TravelEar.Helper'

if (-not $SkipBuild) {
    Write-Host '==> build plugin (Release)'
    dotnet build src/TravelEar/TravelEar.csproj -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' }
    Write-Host '==> publish Helper (Release, single-file, self-contained win-x64)'
    dotnet publish src/TravelEar.Helper/TravelEar.Helper.csproj -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed' }
}

$pluginBin = 'src/TravelEar/bin/Release/net6.0'
if (-not (Test-Path (Join-Path $pluginBin 'TravelEar.dll'))) {
    $pluginBin = (Get-ChildItem 'src/TravelEar/bin/Release' -Directory | Select-Object -First 1).FullName
}
$helperExe = Get-ChildItem 'src/TravelEar.Helper/bin/Release' -Recurse -Filter 'TravelEar.Helper.exe' |
    Where-Object { $_.DirectoryName -like '*publish*' } | Select-Object -First 1
if (-not $helperExe) { throw 'No published TravelEar.Helper.exe under src/TravelEar.Helper/bin/Release/**/publish.' }

Write-Host "==> stage $stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $pluginDir | Out-Null
New-Item -ItemType Directory -Force $helperDir | Out-Null
Copy-Item (Join-Path $pluginBin 'TravelEar.dll') $pluginDir
Copy-Item (Join-Path $pluginBin 'TravelEar.Core.dll') $pluginDir
Copy-Item $helperExe.FullName $helperDir

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "==> zip $zip"
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zip

$sums = Join-Path $dist 'SHA256SUMS.txt'
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content $sums -Encoding ascii

$notesFile = Join-Path $dist "RELEASE-NOTES-v$version.md"
$notes | Set-Content $notesFile -Encoding utf8

Write-Host ''
Write-Host "Assembled ${name}:"
Get-ChildItem $stage -Recurse -File | ForEach-Object { Write-Host ('  {0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($stage.Length + 1)) }
Write-Host "  $hash  $name.zip"
Write-Host ''
Write-Host 'Next (runbook step 6):'
Write-Host "  gh release create v$version `"$zip`" `"$sums`" --title `"TravelEar v$version`" --notes-file `"$notesFile`""
