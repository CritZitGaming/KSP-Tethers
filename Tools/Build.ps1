<#
.SYNOPSIS
    Builds KSP Tethers into KSPTethers/Plugins, runs the offline tests and packages a local release zip.

.DESCRIPTION
    The plugin can't be compiled in CI (it needs KSP's own assemblies), so KSPTethers/Plugins/KSPTethers.dll
    is committed. Run this after changing code or bumping the version, then commit the rebuilt DLL before
    tagging a release. The zip it makes has the same shape as the one the release workflow publishes:
    KSPTethers/ at the root, plus README, LICENSE and CHANGELOG.

.PARAMETER KSPRoot
    Kerbal Space Program install folder (for the reference assemblies).

.PARAMETER SkipTests
    Skip the offline solver/mesh/lifeline tests.

.EXAMPLE
    ./Tools/Build.ps1
    ./Tools/Build.ps1 -KSPRoot "D:\Games\Kerbal Space Program"
#>
param(
    [string]$KSPRoot = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "== Building plugin" -ForegroundColor Cyan
dotnet build "$root\Source\KSPTethers.csproj" -c Release -nologo "-p:KSPRoot=$KSPRoot"
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

if (-not $SkipTests) {
    Write-Host "== Running offline tests" -ForegroundColor Cyan
    dotnet run --project "$root\Tests\RopeSimTests\RopeSimTests.csproj" -c Release -nologo "-p:KSPRoot=$KSPRoot"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Write-Host "== Packaging" -ForegroundColor Cyan
$version = ([xml](Get-Content "$root\Source\KSPTethers.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse "$root\KSPTethers" "$stage\KSPTethers"
Get-ChildItem "$stage\KSPTethers" -Recurse -Include *.pdb, *.mdb | Remove-Item -Force
if (Test-Path "$stage\KSPTethers\PluginData") { Remove-Item -Recurse -Force "$stage\KSPTethers\PluginData" }
Copy-Item "$root\README.md", "$root\LICENSE", "$root\CHANGELOG.md" $stage
$zip = Join-Path $dist "KSPTethers-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# Compress-Archive in Windows PowerShell writes '\' into entry names, which breaks CKAN and non-Windows
# unzip tools, so build the archive by hand with '/' separators.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}
Remove-Item -Recurse -Force $stage
Write-Host "Created $zip" -ForegroundColor Green
