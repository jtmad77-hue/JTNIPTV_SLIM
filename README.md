# IPTV Viewer

A Windows WPF IPTV/Xtream viewer with local caching, favorites, history, continue watching, guide support, and LibVLC playback.

This public source copy does not include any saved accounts, passwords, favorites, history, guide data, or cached channel lists.

## Requirements

- Windows
- .NET 10 SDK

## Run

```powershell
dotnet run
```

## Publish

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-clean.ps1
```

## Installer Package

```powershell
powershell -ExecutionPolicy Bypass -File .\package-installer.ps1
```

This creates:

```text
dist\IptvViewer-Windows-Installer.zip
```

The ZIP contains `install.cmd`, `install.ps1`, `uninstall.cmd`, `uninstall.ps1`, and the app files.

To update an existing install later, build a new ZIP, unzip it, close IPTV Viewer, and run:

```powershell
.\install.cmd
```

The installer replaces the app files and keeps saved user data under `%LOCALAPPDATA%\IptvViewer`.

User data is stored per machine under:

```text
%LOCALAPPDATA%\IptvViewer
```

Use the app's export/import buttons to move saved data between installs.
