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

User data is stored per machine under:

```text
%LOCALAPPDATA%\IptvViewer
```

Use the app's export/import buttons to move saved data between installs.
