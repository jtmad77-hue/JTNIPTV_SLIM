# JTN IPTV Viewer

A Windows WPF IPTV viewer for testing and watching legal IPTV sources. It supports Xtream-style services, free M3U playlists, local caching, favorites, watch history, continue watching, guide data, and LibVLC playback.

This public source copy does not include any private paid IPTV accounts, passwords, favorites, history, guide data, or cached channel lists.

## Project Credits

JTN IPTV Viewer is developed as a joint software project with [WhisperBlinks](https://whisperblinks.io/), contributing application development, product direction, and implementation support.

[JTMTechs.com](https://JTMTechs.com) provides design assistance, Windows distribution support, and public release packaging for the desktop build.

## Active Development

New features, refinements, and usability improvements are planned on a weekly release cadence as the project continues to evolve.

## Features

- Multiple saved IPTV services
- Free starter M3U playlists
- Xtream API support with URL, username, and password
- Live, movies, series, and guide browsing
- Local cache files for faster loading
- Favorites, history, and continue watching
- Import/export saved app data
- Fullscreen and resizable player layout
- LibVLC video playback

## Free Starter Lists

The public build seeds a few free playlist services on first launch:

- Free - IPTV.org USA
- Free - IPTV.org All Countries
- Free - Free-TV Public Playlist

These are M3U playlist sources, so they do not require a username or password. Use **Refresh Live Only** to load their channels. Users can delete these entries if they do not want them.

## Requirements

- Windows 10 or later
- .NET 10 SDK to build from source

Installed/package builds are self-contained and include the .NET runtime and VLC runtime files.

## Run From Source

Open PowerShell in this folder:

```powershell
cd "C:\path\to\IPTV_PUB"
dotnet run
```

## Build

```powershell
dotnet build
```

## Publish A Clean App Folder

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-clean.ps1
```

Output:

```text
dist\IptvViewer
```

Run:

```text
dist\IptvViewer\IptvViewer.exe
```

## Create Installer ZIP

```powershell
powershell -ExecutionPolicy Bypass -File .\package-installer.ps1
```

Output:

```text
dist\IptvViewer-Windows-Installer.zip
```

The ZIP contains:

- `app\` published app files
- `install.cmd`
- `install.ps1`
- `uninstall.cmd`
- `uninstall.ps1`
- `README.txt`

## Create Portable ZIP

```powershell
powershell -ExecutionPolicy Bypass -File .\package-portable.ps1
```

Output:

```text
dist\IptvViewer-Windows-Portable.zip
```

The portable ZIP is for users who do not want a full install. They unzip it and run:

```text
IptvViewer.exe
```

## Install Or Update

Unzip `IptvViewer-Windows-Installer.zip`, then run:

```powershell
.\install.cmd
```

Default install location:

```text
%LOCALAPPDATA%\Programs\IptvViewer
```

To update an existing install:

1. Build a new installer ZIP.
2. Unzip it.
3. Close IPTV Viewer if it is running.
4. Run `install.cmd` again.

The installer replaces the app files but keeps user data.

For a user-friendly setup and first-use walkthrough, open `HOW_TO_USE_WINDOWS.html` in a web browser.

## User Data

User data is stored per Windows user under:

```text
%LOCALAPPDATA%\IptvViewer
```

Typical files include:

- `services.json`
- `favorites.json`
- `history.json`
- `continue_watching.json`
- `cache_*.json`
- `epg_*.json`

These files are intentionally excluded from public packages because they can contain private provider URLs, usernames, passwords, watched items, favorites, and cached channel metadata.

Use **Export Saved Data** and **Import Saved Data** inside the app to move settings to another install.

## Adding A Service

For Xtream services:

1. Click **Add** under Saved Service.
2. Enter account name, server URL, username, and password.
3. Click **Save Service**.
4. Use **Refresh Live Only**, **Refresh Movies Only**, or **Refresh Series Only**.

For M3U playlists:

1. Click **Add**.
2. Enter a name and a direct `.m3u` or `.m3u8` URL.
3. Leave username and password blank.
4. Click **Save Service**.
5. Use **Refresh Live Only**.

## Notes

This app is for lawful IPTV sources only. The project does not provide private paid accounts, copyrighted streams, or hosted video content.

Before publishing a public build, do not copy any files from `%LOCALAPPDATA%\IptvViewer` into the repository or `dist` package folders.
