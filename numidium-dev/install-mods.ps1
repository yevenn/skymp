# Installs downloaded mods into the SkyrimDev test copy, recording every file
# and backing up anything overwritten, so each mod can be removed cleanly.
$ErrorActionPreference = "Stop"
$downloads = "E:\Jarldom\mod-downloads"
$staging   = "E:\Jarldom\mod-staging"
$data      = "E:\Jarldom\SkyrimDev\Data"
$records   = "E:\Jarldom\mod-installs"
$backups   = "E:\Jarldom\mod-backups"

$mods = @(
  @{ Name = "XPMSSE";        Archive = "XP32 Maximum Skeleton Special Extended-1988-5-06-1707663131.7z";
     Folders = @("Skeleton Meshes", "Creature Meshes", "Character Creation\None") },
  @{ Name = "RaceMenu";      Archive = "RaceMenu Anniversary Edition v0-4-20-0-19080-0-4-20-0-1776620918.7z";
     Folders = @("."); Exclude = @("ModderResource") },
  @{ Name = "BodySlide";     Archive = "BodySlide and Outfit Studio - v6.0.0 201 6.0.0 2026-10-13T19-18Z N2AYL5DFr.7z";
     Folders = @("."); Exclude = @("FOMod") },
  @{ Name = "CBBE SFW";      Archive = "CBBE SFW Edition 74257 1.1.4 2026-08-16T16-17Z kATsMWBJX.7z";
     Folders = @("00 Required", "10 Outfits", "11 Outfits BodySlide", "20 Face Pack", "21 Thin + Trimmed Brows", "27 Dirt to Beauty Marks", "30 Underwear") },
  @{ Name = "HIMBO Core";    Archive = "01a) HIMBO V5 - Core (Nevernude Body)-46311-5-8-1772908656.7z"; Folders = @(".") },
  @{ Name = "HIMBO Refits";  Archive = "02) HIMBO V5 - BG-DG-DB Refits-46311-5-8a-1773592661.7z";        Folders = @(".") },
  # Clothing refits for mods in the load order (BodySlide projects, built by Build Numidium Bodies.bat)
  @{ Name = "Sentinel HIMBO";  Archive = "Sentinel - HIMBO Refit-175029-1-1-1774899474.rar"; Folders = @(".") },
  @{ Name = "Sentinel 3BA";    Archive = "Sentinel - 3BA-159370-1-1758045822.zip";
     Folders = @("Sentinel - 3BA"); Exclude = @("meta.ini") },
  # The rest of the archive is the Mage Clothing Expansion we already have, same version
  @{ Name = "MCE BodySlide";   Archive = "Mage Clothing Expansion-128173-2-0-1-1727108300.zip";
     Folders = @("."); Include = @("CalienteTools") },
  @{ Name = "Moon Monk CBBE";  Archive = "Moon Monk's Robes - CBBE Patch-82495-1-0-1673410526.zip"; Folders = @(".") }
)

New-Item -ItemType Directory -Force $staging, $records, $backups | Out-Null
foreach ($mod in $mods) {
  if (Test-Path (Join-Path $records "$($mod.Name).json")) { Write-Host "$($mod.Name): already installed, skipping"; continue }
  $stage = Join-Path $staging $mod.Name
  if (-not (Test-Path $stage)) {
    New-Item -ItemType Directory -Force $stage | Out-Null
    tar -xf (Join-Path $downloads $mod.Archive) -C $stage
    if ($LASTEXITCODE -ne 0) { throw "extracting $($mod.Archive) failed" }
  }

  $installed = @(); $overwritten = @()
  foreach ($folder in $mod.Folders) {
    $src = (Resolve-Path (Join-Path $stage $folder)).Path
    foreach ($file in Get-ChildItem $src -Recurse -File) {
      $rel = $file.FullName.Substring($src.Length).TrimStart('\')
      if ($mod.Exclude -and ($mod.Exclude | Where-Object { $rel -like "$_\*" -or $rel -eq $_ })) { continue }
      if ($mod.Include -and -not ($mod.Include | Where-Object { $rel -like "$_\*" -or $rel -eq $_ })) { continue }
      $dest = Join-Path $data $rel
      if (Test-Path -LiteralPath $dest) {
        $bk = Join-Path (Join-Path $backups $mod.Name) $rel
        New-Item -ItemType Directory -Force (Split-Path $bk) | Out-Null
        Copy-Item -LiteralPath $dest $bk -Force
        $overwritten += $rel
      }
      New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
      Copy-Item -LiteralPath $file.FullName $dest -Force
      $installed += $rel
    }
  }
  @{ mod = $mod.Name; archive = $mod.Archive; folders = $mod.Folders; installedAt = (Get-Date).ToString("s");
     files = $installed; overwrittenWithBackup = $overwritten } | ConvertTo-Json -Depth 4 |
    Set-Content (Join-Path $records "$($mod.Name).json") -Encoding utf8
  Write-Host ("{0}: {1} files installed, {2} existing files backed up and replaced" -f $mod.Name, $installed.Count, $overwritten.Count)
}