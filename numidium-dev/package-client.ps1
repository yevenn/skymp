# Packages Numidium's own client files (SkyrimPlatform, the SkyMP client, our
# SKSE plugins and scripts) from the build for the launcher to install:
#   build\numidium-client\numidium-client-<version>.zip
#   build\numidium-client\numidium-client.json   (version, file, size, sha256)
#   build\numidium-client\numidium-modlist.json  (from numidium-dev)
#   build\numidium-client\NOTICE.md              (licenses and credits; also in the zip,
#                                                 under Numidium\Licenses with the license texts)
#   build\numidium-client\collection.json        (numidium-dev\collection.json, or with -Collection the
#                                                 Nexus collection's archive as Vortex exports or
#                                                 downloads it, or another collection.json)
# SKSE is not included: the launcher fetches it from its own site.
param(
  [string]$Repo       = (Split-Path $PSScriptRoot -Parent),
  [string]$Version    = "",
  [string]$Collection = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = Join-Path $Repo "build\dist\client"
$outDir = Join-Path $Repo "build\numidium-client"
if (-not (Test-Path (Join-Path $source "Data\SKSE\Plugins\SkyrimPlatform.dll"))) { throw "No client build in $source" }

# Version: date and commit. With uncommitted changes the package's own checksum is
# added once it's built, since the launcher reinstalls only when the version changes
$dirty = $false
if (-not $Version) {
  $sha = (& git -C $Repo rev-parse --short HEAD).Trim()
  $dirty = [bool](& git -C $Repo status --porcelain)
  $Version = (Get-Date -Format "yyyy.MM.dd") + "-$sha"
}

# Developer files, per-player settings, and Address Library for game versions we don't run on
$exclude = @(
  "Data\Platform\plugin-example\*",
  "Data\Scripts\Source\*",
  "Data\Platform\Plugins\skymp5-client-settings.txt"
)
$files = Get-ChildItem $source -Recurse -File | ForEach-Object { $_.FullName.Substring($source.Length + 1) } | Where-Object {
  $rel = $_
  -not ($exclude | Where-Object { $rel -like $_ }) -and
  -not ($rel -like "Data\SKSE\Plugins\versionlib-*" -and $rel -notlike "Data\SKSE\Plugins\versionlib-1-6-1170-0*")
} | Sort-Object

New-Item -ItemType Directory -Force $outDir | Out-Null
Get-ChildItem $outDir -Filter "numidium-client-*.zip" | Remove-Item
$zipPath = Join-Path $outDir "numidium-client.zip.tmp"
if (Test-Path $zipPath) { Remove-Item $zipPath }
# Our files from numidium-dev that go into the game copy too
$extra = @{
  "Data\CalienteTools\BodySlide\SliderGroups\Numidium.xml" = (Join-Path $PSScriptRoot "bodyslide\Numidium.xml")
}

# Licenses and credits, in Numidium\Licenses: the GPL/AGPL texts, where the source
# is, and the notices of everything bundled (one per vcpkg package built in)
$licenses = "Numidium\Licenses"
$commit = (& git -C $Repo rev-parse HEAD).Trim()
$sourceState = if ($dirty) { "with uncommitted changes: a test build, not a release" } else { "the release's tag points at it" }
$noticeTemp = Join-Path ([IO.Path]::GetTempPath()) "numidium-NOTICE-$([guid]::NewGuid()).md"
$notice = (Get-Content (Join-Path $PSScriptRoot "NOTICE.md") -Raw).Replace("{commit}", $commit).Replace("{source}", $sourceState)
[IO.File]::WriteAllText($noticeTemp, $notice, (New-Object Text.UTF8Encoding $false))
$extra["$licenses\NOTICE.md"] = $noticeTemp
$extra["$licenses\GPL-3.0.txt"] = Join-Path $Repo "skymp5-client\LICENSE"
$extra["$licenses\AGPL-3.0.txt"] = Join-Path $Repo "skymp5-server\LICENSE"
$extra["$licenses\THIRD_PARTY_LICENSES.txt"] = Join-Path $Repo "THIRD_PARTY_LICENSES"
$vcpkgShare = Join-Path $Repo "build\vcpkg_installed\x64-windows-sp\share"
$notices = @(Get-ChildItem $vcpkgShare -Directory | Where-Object { Test-Path (Join-Path $_.FullName "copyright") })
if ($notices.Count -eq 0) { throw "No package notices in $vcpkgShare; build the client first" }
foreach ($package in $notices) {
  $extra["$licenses\third-party\$($package.Name).txt"] = Join-Path $package.FullName "copyright"
}

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($rel in $files) {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $source $rel), $rel.Replace('\', '/'),
      [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
  foreach ($rel in $extra.Keys) {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $extra[$rel], $rel.Replace('\', '/'),
      [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
} finally {
  $zip.Dispose()
  Remove-Item $noticeTemp -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($dirty) { $Version += "-dirty-" + $hash.Substring(0, 8) }
$zipName = "numidium-client-$Version.zip"
Move-Item $zipPath (Join-Path $outDir $zipName)
$zipPath = Join-Path $outDir $zipName

$info = [ordered]@{
  version = $Version
  file    = $zipName
  size    = (Get-Item $zipPath).Length
  sha256  = $hash
}
[IO.File]::WriteAllText((Join-Path $outDir "numidium-client.json"), ($info | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Write-Host ("{0}: {1} files, {2:n0} MB" -f $zipName, ($files.Count + $extra.Count), ($info.size / 1MB))

Copy-Item (Join-Path $PSScriptRoot "numidium-modlist.json") $outDir -Force
[IO.File]::WriteAllText((Join-Path $outDir "NOTICE.md"), $notice, (New-Object Text.UTF8Encoding $false))
if (-not $Collection) { $Collection = Join-Path $PSScriptRoot "collection.json" }
if (Test-Path $Collection) {
  $target = Join-Path $outDir "collection.json"
  if ($Collection -like "*.json") {
    Copy-Item $Collection $target -Force
  } else {
    # The collection archive Vortex downloads (collection_<slug>_<revision>_<date>.7z)
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("numidium-collection-" + [guid]::NewGuid())
    New-Item -ItemType Directory $temp | Out-Null
    try {
      tar -xf $Collection -C $temp collection.json
      if ($LASTEXITCODE -ne 0) { throw "No collection.json in $Collection" }
      Copy-Item (Join-Path $temp "collection.json") $target -Force
    } finally { Remove-Item $temp -Recurse -Force }
  }
  $mods = (Get-Content $target -Raw | ConvertFrom-Json).mods.Count
  Write-Host "collection.json: $mods mods"
}
