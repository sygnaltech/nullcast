# Nullcast

**Nullcast** is a lightweight, client-side Windows video player built around the
[Nullcast bookmarks service](https://nullcast-api.sygnal.com). It
plays local files, direct URLs, and YouTube videos, and syncs playlists,
bookmarks, and playback position against your Nullcast workspaces.

> **Windows only.** This is a native WPF desktop application and depends on
> Windows-specific frameworks (WPF, Windows Forms, DWM, virtual-desktop COM
> interop). It does **not** run on macOS or Linux — see
> [Is it easy for you to fix](#platform) below.

## Repository layout

This repo contains **two** implementations of the player. They are independent
.NET solutions:

| Folder | Status | Video engine | Notes |
| ------ | ------ | ------------ | ----- |
| [`app-flyleaf/`](app-flyleaf/) | **Active** — current development | [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf) + FFmpeg | This is the app to build, run, and contribute to. |
| [`app-wpf/`](app-wpf/) | **Legacy** — archived for reference | [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) + libVLC | Kept in-tree as a reference implementation. Not maintained. Do not build new features here. |

> **New work goes in `app-flyleaf/`.** `app-wpf/` is retained only so the
> earlier LibVLC-based approach stays available for comparison. Treat it as
> read-only history.

## Features

- Play **local files**, **direct media URLs**, and **YouTube** videos
  (YouTube resolution via [yt-dlp](https://github.com/yt-dlp/yt-dlp), fetched
  automatically on first run).
- Selectable YouTube **quality** (up to 4K / 2160p).
- **Playlist / bookmarks sync** against the Sygnal Playlist service —
  workspaces, YouTube bookmarks, and resume-where-you-left-off playback
  position.
- Local **playback history**.
- **Fullscreen** (F11), **drag-and-drop** to open, **mouse-wheel volume**,
  right-click **"copy video URL"**.
- **Pin to all virtual desktops** and always-on-top support.
- **Remote control API** — a local HTTP + Server-Sent-Events interface so other apps
  (voice agents, a control panel, scripts) can play/pause, change volume, start specific
  items, and read live playback state. See **[`VIDEO-PLAYER-API.md`](VIDEO-PLAYER-API.md)**.
- Indigo Slate visual theme with bundled **Instrument Sans** font.

## Requirements

- **Windows 10/11**
- **.NET 8 SDK** (`net8.0-windows`)
- A code editor / IDE: Visual Studio 2022, JetBrains Rider, or VS Code with the
  C# Dev Kit.

## Build & run

From the `app-flyleaf` directory:

```sh
dotnet run                # Debug
dotnet run -c Release     # Release
```

Or from your IDE:

- **F5** — build + run with debugger
- **Ctrl+F5** — build + run without debugger
- **Ctrl+Shift+B** — build only

A release build produces `bin/Release/net8.0-windows/VideoPlayer.exe`. Point
your Start-menu / desktop shortcut at that path. See
[`app-flyleaf/DEPLOY.md`](app-flyleaf/DEPLOY.md) for deployment details.

### FFmpeg note

FlyleafLib requires the **FFmpeg 8.0** shared libraries. They live in
[`app-flyleaf/FFmpeg/`](app-flyleaf/FFmpeg/) and are copied into the build
output automatically. The `Flyleaf.FFmpeg.Bindings` package version (8.0.1)
must match the FFmpeg DLL major version (8.0) — do not upgrade one without the
other.

## Platform

This is a Win32/WPF application by design. Porting to macOS or Linux would
require replacing the entire UI layer (WPF → e.g. Avalonia or MAUI), the
Windows-native interop, and revisiting the video backend. It is effectively a
rewrite, not a configuration change.

## Contributing

The project is MIT-licensed (see [LICENSE](LICENSE)) and open for the team to
evolve. When contributing:

- Do all new work in **`app-flyleaf/`**. Leave `app-wpf/` alone.
- Keep the FFmpeg binding/DLL versions in lockstep (see above).
- Follow the existing project conventions in
  [`AGENTS.md`](AGENTS.md) / [`CLAUDE.md`](CLAUDE.md).

## License

The original source code in this repository is released under the
[MIT License](LICENSE).

### Third-party components & licenses

This application links against and/or redistributes third-party components that
carry their own licenses. The MIT license on our code does **not** relicense
these — they remain under their respective terms, and those terms are preserved
here. Because the video engines are linked dynamically (LGPL), MIT licensing of
our own code is compatible.

**`app-flyleaf/` (active):**

| Component | Version | License |
| --------- | ------- | ------- |
| [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf) | 3.10.2 | LGPL-3.0-or-later |
| [FlyleafLib.Controls.WPF](https://github.com/SuRGeoNix/Flyleaf) | 1.6.2 | LGPL-3.0-or-later |
| [Flyleaf.FFmpeg.Bindings](https://github.com/SuRGeoNix/Flyleaf.FFmpeg) | 8.0.1 | LGPL-3.0-or-later |
| [FFmpeg](https://ffmpeg.org/) (shared libs, redistributed in `FFmpeg/`) | 8.0 | LGPL-2.1-or-later |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) (downloaded at runtime) | latest | The Unlicense (public domain) |
| [Instrument Sans](https://github.com/Instrument/instrument-sans) (bundled font) | — | SIL Open Font License 1.1 — see [`app-flyleaf/Fonts/OFL.txt`](app-flyleaf/Fonts/OFL.txt) |

**`app-wpf/` (legacy):**

| Component | Version | License |
| --------- | ------- | ------- |
| [LibVLCSharp.WPF](https://code.videolan.org/videolan/LibVLCSharp) | 3.9.5 | LGPL-2.1-or-later |
| [VideoLAN.LibVLC.Windows](https://www.videolan.org/vlc/libvlc.html) (libVLC) | 3.0.23 | LGPL-2.1-or-later |

If you redistribute builds of this application, ensure the corresponding
license texts for the LGPL components (FlyleafLib, FFmpeg, libVLC) and the
Instrument Sans OFL license are included alongside the binaries, per those
licenses' requirements.
