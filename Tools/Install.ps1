<#
.SYNOPSIS
    Copies the KSPTethers folder into a KSP install, replacing the previous copy but keeping the player's
    saved app settings (PluginData/UserSettings.cfg: keys, cable styles, lifeline toggles).

.EXAMPLE
    ./Tools/Install.ps1
    ./Tools/Install.ps1 -KSPRoot "D:\Games\Kerbal Space Program"
#>
param(
    [string]$KSPRoot = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
)

$ErrorActionPreference = "Stop"
$source = Join-Path (Split-Path -Parent $PSScriptRoot) "KSPTethers"
$gameData = Join-Path $KSPRoot "GameData"

if (-not (Test-Path "$source\Plugins\KSPTethers.dll")) { throw "Build first: KSPTethers.dll is missing (run ./Tools/Build.ps1)" }
if (-not (Test-Path $gameData)) { throw "No GameData folder at $gameData" }
if (-not (Test-Path "$gameData\ModuleManager*.dll")) { Write-Warning "ModuleManager was not found in GameData - KSP Tethers needs it." }

$target = Join-Path $gameData "KSPTethers"
$userSettings = Join-Path $target "PluginData\UserSettings.cfg"
# Keep the exact bytes: re-encoding the file (e.g. with Set-Content) would add a byte-order mark.
$saved = $null
if (Test-Path $userSettings) { $saved = [IO.File]::ReadAllBytes($userSettings) }

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Copy-Item -Recurse $source $target

if ($saved -ne $null) {
    New-Item -ItemType Directory -Force (Split-Path $userSettings) | Out-Null
    [IO.File]::WriteAllBytes($userSettings, $saved)
    Write-Host "Kept your saved app settings (PluginData/UserSettings.cfg)"
}
Write-Host "Installed to $target" -ForegroundColor Green
