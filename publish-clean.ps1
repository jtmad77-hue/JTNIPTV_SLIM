param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = ".\dist\IptvViewer"
)

$ErrorActionPreference = "Stop"

if (Test-Path $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}

dotnet publish .\IptvViewer.csproj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $Output

$readme = @"
IPTV Viewer

Run:
  IptvViewer.exe

This distributable does not include saved accounts, favorites, history, or cached channel lists.
User data is stored on each user's machine under:
  %LOCALAPPDATA%\IptvViewer

Use Export Saved Data / Import Saved Data inside the app to move settings to a new install.
"@

Set-Content -LiteralPath (Join-Path $Output "README.txt") -Value $readme

Write-Host "Published clean distributable to: $((Resolve-Path $Output).Path)"
