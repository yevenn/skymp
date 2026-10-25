# Runs the Numidium server outside any other program, restarting it when it
# stops: a crash, or the process being killed. Output goes to dated log files in
# build\dist\server\logs (the last $KeepLogs are kept); this window shows when it
# starts, stops and restarts. Close the window or press Ctrl+C to stop for good.
#
# Started from "Start Numidium Server.bat". To start it with Windows, put a
# shortcut to that .bat in the Startup folder (Win+R, shell:startup).
param(
  [string]$ServerDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "build\dist\server"),
  [int]$KeepLogs = 30,
  [int]$MaxRuns = 0  # 0: forever. For testing: stop after this many runs
)
$ErrorActionPreference = "Stop"
$Host.UI.RawUI.WindowTitle = "Numidium server"

$entry = Join-Path $ServerDir "dist_back\skymp5-server.js"
if (-not (Test-Path $entry)) { throw "No server build at $entry" }
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw "Node.js isn't installed (no node on PATH)" }

# Another server (or anything else) on the ports would make this one fail at once
$busy = @(Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue) +
        @(Get-NetUDPEndpoint -LocalPort 7777 -ErrorAction SilentlyContinue)
if ($busy.Count -gt 0) {
  $owners = ($busy | ForEach-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } | Sort-Object -Unique) -join ", "
  Write-Host "Port 3000 or 7777 is already in use (by $owners): is a Numidium server already running?" -ForegroundColor Red
  Read-Host "Press Enter to close"
  exit 1
}

$logDir = Join-Path $ServerDir "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
# The server finds its settings, data and world in its working folder
Set-Location $ServerDir
$runs = 0
$quickExits = 0
while ($true) {
  Get-ChildItem $logDir -Filter "server-*.log" | Sort-Object Name -Descending | Select-Object -Skip $KeepLogs | Remove-Item
  $log = Join-Path $logDir ("server-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
  $started = Get-Date
  Write-Host ("[{0:HH:mm:ss}] Starting the server; log: {1}" -f $started, $log)
  # cmd does the redirection: Windows PowerShell would wrap every stderr line as an error
  & cmd /c "`"$node`" dist_back\skymp5-server.js >> `"$log`" 2>&1" | Out-Null
  $code = $LASTEXITCODE
  $ran = (Get-Date) - $started
  Write-Host ("[{0:HH:mm:ss}] The server stopped (exit code {1}) after {2:hh\:mm\:ss}." -f (Get-Date), $code, $ran) -ForegroundColor Yellow
  $runs++
  if ($MaxRuns -gt 0 -and $runs -ge $MaxRuns) { break }
  # Crashing right after starting again and again: wait longer before the next try
  if ($ran.TotalSeconds -lt 60) { $quickExits++ } else { $quickExits = 0 }
  $wait = if ($quickExits -ge 3) { 60 } else { 5 }
  Write-Host "Restarting in $wait seconds (close this window to stop)..."
  Start-Sleep -Seconds $wait
}
