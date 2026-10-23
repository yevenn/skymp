# Packages Numidium's own client files (SkyrimPlatform, the SkyMP client, our
# SKSE plugins and scripts) from the build for the launcher to install:
#   build\numidium-client\numidium-client-<version>.zip
#   build\numidium-client\numidium-client.json   (version, file, size, sha256)
# SKSE is not included: the launcher fetches it from its own site.
param(
  [string]$Repo    = (Split-Path $PSScriptRoot -Parent),
  [string]$Version = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = Join-Path $Repo "build\dist\client"
$outDir = Join-Path $Repo "build\numidium-client"
if (-not (Test-Path (Join-Path $source "Data\SKSE\Plugins\SkyrimPlatform.dll"))) { throw "No client build in $source" }

if (-not $Version) {
  $sha = (& git -C $Repo rev-parse --short HEAD).Trim()
  $dirty = if (& git -C $Repo status --porcelain) { "-dirty" } else { "" }
  $Version = (Get-Date -Format "yyyy.MM.dd") + "-$sha$dirty"
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
$zipName = "numidium-client-$Version.zip"
$zipPath = Join-Path $outDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($rel in $files) {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $source $rel), $rel.Replace('\', '/'),
      [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
} finally { $zip.Dispose() }

$info = [ordered]@{
  version = $Version
  file    = $zipName
  size    = (Get-Item $zipPath).Length
  sha256  = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $outDir "numidium-client.json"), ($info | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Write-Host ("{0}: {1} files, {2:n0} MB" -f $zipName, $files.Count, ($info.size / 1MB))
