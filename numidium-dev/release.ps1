# Makes a Numidium release from the current commit: packages the client
# (package-client.ps1), builds the launcher stamped with the same version, and
# gathers what the launcher downloads into build\numidium-release\<version>:
#   Numidium.Launcher.exe, numidium-client.json, numidium-client-<version>.zip,
#   collection.json, numidium-modlist.json
# With -Publish it creates the GitHub release numidium-<version> on yevenn/skymp
# with those files, through the GitHub CLI (gh, signed in with `gh auth login`).
# Without it, upload them yourself at https://github.com/yevenn/skymp/releases/new
# with that tag. The launcher picks the newest release that has numidium-client.json.
#
# The client is packaged from build\dist\client as it is: build it from this
# commit first.
param(
  [string]$Repo  = (Split-Path $PSScriptRoot -Parent),
  [string]$Notes = "",
  [switch]$Publish
)
$ErrorActionPreference = "Stop"

if (& git -C $Repo status --porcelain) { throw "The repo has uncommitted changes; commit them first, a release is made from a commit." }
$sha = (& git -C $Repo rev-parse HEAD).Trim()
if ($Publish) {
  & git -C $Repo fetch -q origin
  if (-not (& git -C $Repo branch -r --contains $sha | Select-String "origin/")) { throw "Push $sha to origin first: the release tag points at it." }
}

& (Join-Path $PSScriptRoot "package-client.ps1") -Repo $Repo
$packageDir = Join-Path $Repo "build\numidium-client"
$info = Get-Content (Join-Path $packageDir "numidium-client.json") -Raw | ConvertFrom-Json
$version = $info.version
$tag = "numidium-$version"

$out = Join-Path $Repo "build\numidium-release\$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$launcher = Join-Path $Repo "numidium-launcher\src\Numidium.Launcher"
$publishDir = Join-Path $Repo "build\numidium-release\launcher-publish"
& dotnet publish $launcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:InformationalVersion=$version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Building the launcher failed" }
Copy-Item (Join-Path $publishDir "Numidium.Launcher.exe") $out

foreach ($name in @("numidium-client.json", $info.file, "collection.json", "numidium-modlist.json")) {
  $from = Join-Path $packageDir $name
  if (-not (Test-Path $from)) { throw "$name is missing from $packageDir" }
  Copy-Item $from $out
}
$files = Get-ChildItem $out -File
$files | ForEach-Object { Write-Host ("  {0,-40} {1,8:n1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host "Release $tag ready in $out"

if (-not $Publish) {
  Write-Host "Not published. To publish: run again with -Publish, or upload these files at"
  Write-Host "https://github.com/yevenn/skymp/releases/new with tag $tag (target $sha)."
  return
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "The GitHub CLI (gh) isn't installed; upload the files by hand instead." }
if (-not $Notes) { $Notes = "Numidium client $version, from commit $sha." }
& gh release create $tag @($files.FullName) --repo yevenn/skymp --target $sha --title "Numidium $version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Published https://github.com/yevenn/skymp/releases/tag/$tag"
