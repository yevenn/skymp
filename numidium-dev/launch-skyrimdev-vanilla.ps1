# Launches the SkyrimDev test copy with its own load order.
# Skyrim keeps one plugins.txt in %LOCALAPPDATA% that every install shares,
# so this swaps in the test one and restores Keizaal's afterwards.
#
# Plugins listed in skyrimdev-plugins.txt next to this script (one file name
# per line, in load order, matching the server's loadOrder) are enabled on top
# of the base game. Without that file only the base game loads.

$ErrorActionPreference = "Stop"

$gameDir    = "E:\Jarldom\SkyrimDev"
$pluginList = Join-Path $PSScriptRoot "skyrimdev-plugins.txt"
$pluginsTxt = Join-Path $env:LOCALAPPDATA "Skyrim Special Edition\plugins.txt"
$backupDir  = "E:\Jarldom\plugins-backups"
$backup     = Join-Path $backupDir ("plugins.txt." + (Get-Date -Format "yyyy-MM-dd_HH-mm-ss"))

if (Get-Process SkyrimSE -ErrorAction SilentlyContinue) {
  Write-Host "Skyrim is already running. Close it first." -ForegroundColor Red
  exit 1
}

New-Item -ItemType Directory -Force $backupDir | Out-Null
Copy-Item $pluginsTxt $backup
Write-Host "Saved your plugins.txt to $backup"

try {
  # Skyrim.esm, Update.esm and the three DLCs load implicitly
  $lines = @("# SkyrimDev test load order")
  if (Test-Path $pluginList) {
    foreach ($plugin in Get-Content $pluginList) {
      $plugin = $plugin.Trim()
      if ($plugin -and -not $plugin.StartsWith("#")) {
        if (-not (Test-Path (Join-Path $gameDir "Data\$plugin"))) { throw "$plugin is not in $gameDir\Data" }
        $lines += "*$plugin"
      }
    }
  }
  Set-Content -Path $pluginsTxt -Value $lines -Encoding ascii
  Write-Host ("Enabled plugins: " + $(if ($lines.Count -gt 1) { ($lines[1..($lines.Count - 1)] -replace '^\*', '') -join ', ' } else { "base game only" }))

  Write-Host "Starting SkyrimDev through SKSE..."
  Start-Process (Join-Path $gameDir "skse64_loader.exe") -WorkingDirectory $gameDir

  # skse64_loader exits right after starting SkyrimSE.exe, so wait for the game itself
  $game = $null
  for ($i = 0; $i -lt 120 -and -not $game; $i++) {
    Start-Sleep -Milliseconds 500
    $game = Get-Process SkyrimSE -ErrorAction SilentlyContinue |
      Where-Object { $_.Path -like "$gameDir\*" } | Select-Object -First 1
  }
  if (-not $game) { throw "SkyrimSE.exe from $gameDir did not start within 60 seconds." }

  Write-Host "Game running (PID $($game.Id)). Leave this window open - it restores plugins.txt when the game closes."
  $game.WaitForExit()
  Write-Host "Game closed."
}
finally {
  Copy-Item $backup $pluginsTxt -Force
  Write-Host "Restored your Keizaal plugins.txt." -ForegroundColor Green
}
