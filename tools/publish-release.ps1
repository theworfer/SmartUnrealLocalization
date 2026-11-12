# Builds the release zip: artifacts/SmartUnrealLocalization-<version>-win-x64.zip
#
#   powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1
#   powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1 -SkipTests
#
# Steps: run every test, publish the self-contained single-file exe
# (Properties/PublishProfiles/win-x64.pubxml), check it starts, then zip it with
# the license, the third-party notices and the user docs.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content -Raw 'SmartUnrealLocalization.csproj'
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in SmartUnrealLocalization.csproj' }
Write-Host "Smart Unreal Localization $version"

if (-not $SkipTests) {
    Write-Host '== tests'
    dotnet test --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - no release.' }
}

Write-Host '== publish'
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish SmartUnrealLocalization.csproj -p:PublishProfile=win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $publishDir 'SmartUnrealLocalization.exe'
if (-not (Test-Path $exe)) { throw "No exe at $exe" }
$info = (Get-Item $exe).VersionInfo
if ($info.ProductVersion -notlike "$version*") { throw "The exe reports $($info.ProductVersion), expected $version" }

# Start check without touching anyone's profile: --version prints and exits
# before the window, the config or the MCP pipe are created.
$out = & $exe --version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $out -notmatch [regex]::Escape($version)) { throw "--version check failed: $out" }
Write-Host "   starts: $($out.Trim())"

Write-Host '== package'
$stage = Join-Path $root "artifacts\stage\SmartUnrealLocalization-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'docs') | Out-Null
Copy-Item $exe $stage
Copy-Item 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.md', 'CHANGELOG.md' $stage
Copy-Item 'licenses' $stage -Recurse
Copy-Item 'docs\USER-GUIDE.md', 'docs\USER-GUIDE.uk.md', 'docs\FAQ.md', 'docs\TROUBLESHOOTING.md' (Join-Path $stage 'docs')

$zip = Join-Path $root "artifacts\SmartUnrealLocalization-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# Not Compress-Archive: on Windows PowerShell 5.1 it stores 'docs\x' with backslashes,
# which unzip tools outside Windows turn into file names. Entries are named by hand with '/'.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii
Write-Host "== done: $zip"
Write-Host "   SHA256 $hash"
Write-Host ("   size {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
