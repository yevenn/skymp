# Launches the SkyrimDev test copy with a base-game-only load order.
# Skyrim keeps one plugins.txt in %LOCALAPPDATA% that every install shares,
# so this swaps in an empty one for the test and restores Keizaal's afterwards.

$ErrorActionPreference = "Stop"

$gameDir    = "E:\Jarldom\SkyrimDev"
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
  # Skyrim.esm, Update.esm and the three DLCs load implicitly; nothing else is enabled.
  Set-Content -Path $pluginsTxt -Value "# SkyrimDev base-game test" -Encoding ascii

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
