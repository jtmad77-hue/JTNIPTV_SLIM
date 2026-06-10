param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = ".\dist\IptvViewer-Portable",
    [string]$ZipPath = ".\dist\IptvViewer-Windows-Portable.zip"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "IptvViewer.csproj"
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Output))
$zipFullPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $ZipPath))
$distPath = Join-Path $PSScriptRoot "dist"

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
New-Item -ItemType Directory -Path $distPath -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($Runtime -eq "win-x64") {
    foreach ($unusedLibVlcRuntime in @("win-x86", "win-arm64")) {
        $unusedLibVlcPath = Join-Path $outputPath "libvlc\$unusedLibVlcRuntime"
        if (Test-Path -LiteralPath $unusedLibVlcPath) {
            Remove-Item -LiteralPath $unusedLibVlcPath -Recurse -Force
        }
    }
}

Get-ChildItem -LiteralPath $outputPath -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Get-ChildItem -LiteralPath $outputPath -Recurse -Filter "*.lib" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$readme = @"
JTN IPTV Viewer - Portable Windows Build

Run:
  IptvViewer.exe

This version does not install anything, does not create shortcuts, and can be
deleted by removing this folder.

User data is not included in this package. Saved accounts, favorites, history,
continue watching, guide data, and cache are stored on each computer under:
  %LOCALAPPDATA%\IptvViewer

Do not add services.json, favorites.json, history.json, cache_*.json, or
epg_*.json to this portable package. Those files may contain private provider
URLs, usernames, passwords, watched items, and cached channel metadata.

Use Export Saved Data and Import Saved Data inside the app to move settings.
"@

Set-Content -LiteralPath (Join-Path $outputPath "README.txt") -Value $readme

if (Test-Path -LiteralPath $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force
}

Compress-Archive -Path (Join-Path $outputPath "*") -DestinationPath $zipFullPath -Force

Write-Host "Created portable folder: $outputPath"
Write-Host "Created portable zip: $zipFullPath"
