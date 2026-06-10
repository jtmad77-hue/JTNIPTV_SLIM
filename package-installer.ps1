param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PackageRoot = ".\dist\installer-package",
    [string]$ZipPath = ".\dist\IptvViewer-Windows-Installer.zip"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "IptvViewer.csproj"
$packageRootPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PackageRoot))
$zipFullPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $ZipPath))
$appPath = Join-Path $packageRootPath "app"
$distPath = Join-Path $PSScriptRoot "dist"

if (Test-Path $packageRootPath) {
    Remove-Item -LiteralPath $packageRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $appPath -Force | Out-Null
New-Item -ItemType Directory -Path $distPath -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $appPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($Runtime -eq "win-x64") {
    foreach ($unusedLibVlcRuntime in @("win-x86", "win-arm64")) {
        $unusedLibVlcPath = Join-Path $appPath "libvlc\$unusedLibVlcRuntime"
        if (Test-Path $unusedLibVlcPath) {
            Remove-Item -LiteralPath $unusedLibVlcPath -Recurse -Force
        }
    }
}

Get-ChildItem -LiteralPath $appPath -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Get-ChildItem -LiteralPath $appPath -Recurse -Filter "*.lib" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$installScript = @'
param(
    [string]$InstallPath = "$env:LOCALAPPDATA\Programs\IptvViewer",
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

$sourceApp = Join-Path $PSScriptRoot "app"
if (-not (Test-Path (Join-Path $sourceApp "IptvViewer.exe"))) {
    throw "Could not find app\IptvViewer.exe next to this installer script."
}

$wasInstalled = Test-Path (Join-Path $InstallPath "IptvViewer.exe")

$runningProcess = Get-Process IptvViewer -ErrorAction SilentlyContinue
if ($runningProcess) {
    throw "IptvViewer is currently running. Close the app, then run this installer again."
}

if (Test-Path $InstallPath) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
Copy-Item -Path (Join-Path $sourceApp "*") -Destination $InstallPath -Recurse -Force

$exePath = Join-Path $InstallPath "IptvViewer.exe"
$shell = New-Object -ComObject WScript.Shell

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\IptvViewer"
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null

$startShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "IPTV Viewer.lnk"))
$startShortcut.TargetPath = $exePath
$startShortcut.WorkingDirectory = $InstallPath
$startShortcut.Description = "IPTV Viewer"
$startShortcut.Save()

if (-not $NoDesktopShortcut) {
    $desktopShortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "IPTV Viewer.lnk"))
    $desktopShortcut.TargetPath = $exePath
    $desktopShortcut.WorkingDirectory = $InstallPath
    $desktopShortcut.Description = "IPTV Viewer"
    $desktopShortcut.Save()
}

if ($wasInstalled) {
    Write-Host "Updated IPTV Viewer at: $InstallPath"
}
else {
    Write-Host "Installed IPTV Viewer to: $InstallPath"
}

Write-Host "Saved accounts, favorites, history, and cache are stored separately under: $env:LOCALAPPDATA\IptvViewer"
'@

$uninstallScript = @'
param(
    [string]$InstallPath = "$env:LOCALAPPDATA\Programs\IptvViewer",
    [switch]$RemoveUserData
)

$ErrorActionPreference = "Stop"

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "IPTV Viewer.lnk"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\IptvViewer"

if (Test-Path $desktopShortcut) {
    Remove-Item -LiteralPath $desktopShortcut -Force
}

if (Test-Path $startMenuDir) {
    Remove-Item -LiteralPath $startMenuDir -Recurse -Force
}

if (Test-Path $InstallPath) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

if ($RemoveUserData) {
    $userDataPath = Join-Path $env:LOCALAPPDATA "IptvViewer"
    if (Test-Path $userDataPath) {
        Remove-Item -LiteralPath $userDataPath -Recurse -Force
    }
}

Write-Host "Uninstalled IPTV Viewer."
'@

$readme = @"
IPTV Viewer Windows Installer Package

Install:
  install.cmd

Update later:
  Close IPTV Viewer, then run install.cmd from a newer package. The installer
  replaces the app files and keeps user data.

Or:
  powershell -ExecutionPolicy Bypass -File .\install.ps1

Uninstall:
  uninstall.cmd

Or:
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1

The app installs per user to:
  %LOCALAPPDATA%\Programs\IptvViewer

User data is not included in this package. Saved accounts, favorites, history,
continue watching, guide data, and cache are stored on each computer under:
  %LOCALAPPDATA%\IptvViewer

Do not add services.json, favorites.json, history.json, cache_*.json, or
epg_*.json to this installer package. Those files may contain private provider
URLs, usernames, passwords, watched items, and cached channel metadata.

Use Export Saved Data and Import Saved Data inside the app to move settings.
"@

$installCmd = @"
@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
"@

$uninstallCmd = @"
@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
"@

Set-Content -LiteralPath (Join-Path $packageRootPath "install.ps1") -Value $installScript
Set-Content -LiteralPath (Join-Path $packageRootPath "uninstall.ps1") -Value $uninstallScript
Set-Content -LiteralPath (Join-Path $packageRootPath "README.txt") -Value $readme
Set-Content -LiteralPath (Join-Path $packageRootPath "install.cmd") -Value $installCmd
Set-Content -LiteralPath (Join-Path $packageRootPath "uninstall.cmd") -Value $uninstallCmd

if (Test-Path $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force
}

Compress-Archive -Path (Join-Path $packageRootPath "*") -DestinationPath $zipFullPath -Force

Write-Host "Created installer package folder: $packageRootPath"
Write-Host "Created installer zip: $zipFullPath"
