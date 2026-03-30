# Migration Plan: WPF to Cross-Platform Video Player

## Background

The current video player (`app-wpf`) is a .NET 8.0 WPF desktop application (~1,650 lines of C#) using LibVLCSharp for video playback and yt-dlp for YouTube stream extraction. It includes OAuth 2.0 playlist sync, quality selection (360p-4K), position tracking, drag-drop, fullscreen, and a dark-themed UI with a collapsible playlist panel.

This document evaluates migrating to a cross-platform architecture using either **Tauri** or **Electron**, with **mpv** as the video playback engine.

### Motivation

| Concern | Current State (WPF) | Target State |
|---------|---------------------|--------------|
| Platform support | Windows only | Windows + macOS (+ Linux possible) |
| Runtime dependency | .NET 8.0 Desktop Runtime required | Self-contained app bundle |
| Video engine | LibVLCSharp (VLC) | mpv (better stream handling, lighter) |
| UI framework | XAML + Win32 interop hacks | HTML/CSS/JS (simpler theming, layout) |
| Bundle size | ~50MB (VLC DLLs + .NET) | Tauri ~35MB / Electron ~180MB |
| Distribution | Manual build + deploy | Standard installers per platform |

---

## Video Engine: Why mpv

Both Electron and Tauri approaches use **mpv** as the playback engine. This is the single most important architectural decision and is independent of the shell framework.

### mpv vs LibVLC comparison

| Capability | LibVLC (current) | mpv (proposed) |
|------------|-----------------|----------------|
| Split audio+video streams | `:input-slave={url}` | `--audio-file={url}` (native, cleaner) |
| Seeking accuracy | Frame-level, occasional stalls | Frame-level, more responsive |
| Quality switching | Stop + restart with new URL | Playlist replacement or restart |
| Codec support | Broad (ships own codecs) | Broad (uses ffmpeg internally) |
| Hardware acceleration | DXVA2, D3D11 | DXVA2, D3D11, VideoToolbox (macOS) |
| Subtitle support | Full | Full + better ASS/SSA rendering |
| Embeddability | HwndHost (WPF), requires Win32 hacks | JSON IPC or `--wid` window embedding |
| macOS support | LibVLCSharp.Mac (separate package) | Native, first-class |
| Binary size | ~40MB (win-x64 + win-x86) | ~30MB per platform |
| Community/maintenance | Active (VideoLAN) | Active, powers IINA, mpv.net, celluloid |

### mpv integration pattern

mpv runs as a child process with a JSON IPC socket. The app sends commands and receives events over this socket.

```
App Shell (Tauri/Electron)
    |
    |-- spawns mpv with:
    |     --input-ipc-server=\\.\pipe\mpv-ipc  (Windows)
    |     --input-ipc-server=/tmp/mpv-ipc       (macOS/Linux)
    |     --wid=<hwnd>                           (embed in app window)
    |     --idle=yes                             (start without file)
    |     --no-terminal
    |
    |-- JSON IPC commands:
    |     { "command": ["loadfile", "https://...video-url"] }
    |     { "command": ["set_property", "audio-files", "https://...audio-url"] }
    |     { "command": ["seek", 30, "relative"] }
    |     { "command": ["set_property", "pause", true] }
    |
    |-- Observes properties via IPC:
          time-pos, duration, pause, eof-reached, volume
```

### yt-dlp integration (unchanged)

The yt-dlp workflow is identical regardless of framework. yt-dlp is spawned as a child process:

```bash
yt-dlp --get-title <url>                              # get title
yt-dlp -f "bestvideo[height<=1080]/bestvideo" -g <url> # get video stream URL
yt-dlp -f "bestaudio" -g <url>                         # get audio stream URL
```

This maps directly to `child_process.spawn` (Electron/Node) or `std::process::Command` (Tauri/Rust).

---

## Approach A: Tauri + mpv (Recommended)

### Overview

Tauri uses the system WebView (WebView2 on Windows, WKWebView on macOS) for the UI layer and Rust for the backend. The app is small (~5MB base + mpv binary), fast to start, and cross-platform.

### Architecture

```
┌─────────────────────────────────────────┐
│  System WebView (HTML/CSS/JS frontend)  │
│  - Playlist panel                       │
│  - Controls bar                         │
│  - Settings/dialogs                     │
│  - Dark theme (pure CSS)               │
├─────────────────────────────────────────┤
│  Tauri Rust Backend                     │
│  - mpv process management + IPC         │
│  - yt-dlp process management            │
│  - OAuth 2.0 PKCE flow                  │
│  - Playlist API client (reqwest)        │
│  - Settings persistence (serde + JSON)  │
│  - Window management / fullscreen       │
│  - Platform-specific: tray, shortcuts   │
└─────────────────────────────────────────┘
```

### Tech stack

| Layer | Technology |
|-------|-----------|
| Frontend | TypeScript, HTML, CSS (or a light framework: Solid/Svelte/vanilla) |
| Backend | Rust |
| Video | mpv (child process + JSON IPC) |
| YouTube | yt-dlp (child process) |
| HTTP client | reqwest (Rust) |
| Serialization | serde + serde_json |
| Window mgmt | Tauri window API |
| Build/bundle | Tauri CLI (`cargo tauri build`) |
| Installer | NSIS (Windows), DMG (macOS), AppImage (Linux) |

### Feature mapping: WPF to Tauri

| WPF Feature | Tauri Equivalent |
|-------------|-----------------|
| LibVLCSharp video rendering | mpv `--wid` embed into WebView or overlay window |
| XAML dark theme + custom controls | CSS variables, flexbox, standard HTML controls |
| Win32 popup menu (quality) | HTML context menu (CSS-styled) |
| WM_PARENTNOTIFY hooks | Tauri window event listeners or transparent overlay |
| Virtual desktop pinning | Drop (no macOS equivalent, niche feature) |
| Drag-drop URLs | HTML5 drag-drop API + Tauri file drop |
| Fullscreen toggle | `window.set_fullscreen()` Tauri API |
| OAuth local HTTP listener | `tauri::http` or `tokio::net::TcpListener` |
| AppSettings JSON | serde to `app_data_dir()/settings.json` |
| Token persistence | serde to `app_data_dir()/tokens.json` |
| Keyboard shortcuts | Tauri global shortcuts API + JS key listeners |
| Progress slider (seeking) | HTML `<input type="range">` + JS |
| Volume slider | HTML `<input type="range">` + JS |
| Playlist ListBox | HTML list with CSS scroll styling |
| Bookmark progress bars | HTML `<progress>` or CSS width percentage |
| Status overlay text | Absolutely-positioned HTML div |
| Open URL dialog | Tauri dialog API or HTML modal |

### Pros

- **~5MB base bundle** (+ ~30MB mpv binary = ~35MB total vs 50MB+ current)
- **Rust backend** is fast, memory-safe, and has excellent async support (tokio)
- **System WebView** means no bundled Chromium; uses OS-provided rendering
- **Native installers** per platform via Tauri CLI
- Tauri v2 has mature APIs for windows, menus, tray, system dialogs, global shortcuts
- Strong TypeScript + Rust type bridge via `#[tauri::command]`

### Cons

- **Rust learning curve** if unfamiliar (though the backend is ~500-800 lines of Rust)
- **mpv window embedding** with WebView is the hardest integration point:
  - On Windows: `--wid` with the HWND of a transparent panel works but requires careful z-ordering
  - On macOS: `--wid` with NSView handle; WKWebView compositing can conflict
  - Alternative: render mpv in a separate child window positioned behind the WebView, with a transparent "hole" in the HTML for the video area
- **WebView2 required on Windows** (pre-installed on Windows 11, auto-installed on Windows 10 by Tauri)
- Smaller ecosystem than Electron for media-related packages

### mpv embedding strategy (Tauri-specific)

The recommended approach for Tauri + mpv:

**Option 1: Overlay window (simpler)**
- Create a Tauri child window (no decorations) for mpv rendering via `--wid`
- Position it behind/beside the main window
- Main window manages all UI; child window is pure video
- Tauri window API handles positioning and fullscreen sync

**Option 2: Single window with transparent hole**
- Main Tauri window has a transparent region where video appears
- mpv renders to the window's HWND with coordinates matching the transparent region
- More complex but feels more integrated

Option 1 is recommended for initial implementation.

---

## Approach B: Electron + mpv

### Overview

Electron bundles Chromium + Node.js. The app uses Node.js to manage mpv and yt-dlp as child processes, and Chromium to render the UI.

### Architecture

```
┌─────────────────────────────────────────┐
│  Chromium Renderer (HTML/CSS/JS)        │
│  - Playlist panel                       │
│  - Controls bar                         │
│  - Settings/dialogs                     │
│  - Dark theme (pure CSS)               │
├─────────────────────────────────────────┤
│  Node.js Main Process                   │
│  - mpv process management + IPC         │
│  - yt-dlp process management            │
│  - OAuth 2.0 PKCE flow                  │
│  - Playlist API client (fetch/axios)    │
│  - Settings persistence (fs + JSON)     │
│  - Window management / fullscreen       │
│  - IPC bridge to renderer               │
└─────────────────────────────────────────┘
```

### Tech stack

| Layer | Technology |
|-------|-----------|
| Frontend | TypeScript, HTML, CSS (or React/Svelte/vanilla) |
| Backend | Node.js (main process) |
| Video | mpv (child process + JSON IPC) |
| YouTube | yt-dlp (child process via child_process) |
| HTTP client | Node fetch or axios |
| Window mgmt | Electron BrowserWindow API |
| Build/bundle | electron-builder or electron-forge |
| Installer | NSIS/Squirrel (Windows), DMG (macOS), AppImage (Linux) |

### Feature mapping: WPF to Electron

| WPF Feature | Electron Equivalent |
|-------------|-------------------|
| LibVLCSharp video rendering | mpv `--wid` with BrowserWindow HWND |
| XAML dark theme | CSS (identical to Tauri) |
| Win32 popup menu | Electron Menu.buildFromTemplate() or HTML menu |
| Virtual desktop pinning | Drop feature |
| Drag-drop URLs | HTML5 drag-drop + Electron webContents events |
| Fullscreen toggle | `win.setFullScreen(true)` |
| OAuth local HTTP listener | Node http.createServer on localhost |
| AppSettings JSON | electron-store or fs.writeFileSync |
| Keyboard shortcuts | Electron globalShortcut + renderer key events |
| All UI controls | HTML/CSS (identical to Tauri) |

### Existing mpv + Electron projects for reference

- **mpv.js** — Node.js bindings using libmpv C API (direct embedding, but less maintained)
- **Stremio** — production Electron app using mpv for playback
- Custom IPC approach — most common and most reliable

### Pros

- **Largest ecosystem** — most npm packages, most community examples
- **Node.js backend** — JavaScript/TypeScript everywhere, lowest learning curve
- **Mature mpv integration patterns** — well-documented by community
- **BrowserWindow HWND** is straightforward to pass to mpv `--wid`
- **electron-builder** produces polished installers with auto-update support

### Cons

- **~150-200MB bundle** (Chromium + Node.js + mpv) vs ~35MB for Tauri
- **Higher memory usage** — Chromium overhead even for simple UIs (~80-150MB baseline)
- **Slower startup** — Chromium initialization takes 1-3 seconds
- **Security surface** — larger attack surface than Tauri's sandboxed WebView
- Chromium version management and updates add maintenance burden

---

## Side-by-Side Comparison

| Dimension | Tauri + mpv | Electron + mpv |
|-----------|------------|----------------|
| Bundle size | ~35MB | ~180MB |
| Memory at idle | ~30-50MB | ~100-180MB |
| Startup time | <1s | 1-3s |
| Backend language | Rust | JavaScript/TypeScript |
| Frontend language | JS/TS (any framework) | JS/TS (any framework) |
| Learning curve | Medium (Rust) | Low (all JS/TS) |
| mpv embedding difficulty | Medium-Hard | Medium |
| Cross-platform maturity | Good (Tauri v2) | Excellent |
| Auto-update | Tauri updater plugin | electron-updater (mature) |
| Community/ecosystem | Growing | Large |
| Native feel | Better (system WebView) | Good |
| Installer output | NSIS, DMG, AppImage, deb | NSIS, DMG, AppImage, deb, snap |

---

## Migration Plan (Tauri path)

### Phase 1: Scaffold and playback

Set up the Tauri v2 project, integrate mpv, and achieve basic video playback.

- [ ] Initialize Tauri v2 project (`cargo create-tauri-app`)
- [ ] Set up frontend (vanilla TS or Svelte — keep it minimal)
- [ ] Bundle mpv binary as a sidecar resource
- [ ] Implement mpv process spawning with JSON IPC (Rust backend)
- [ ] Implement basic playback commands: load, play, pause, seek, volume
- [ ] Wire up property observation: time-pos, duration, pause, eof-reached
- [ ] Build minimal UI: video area + play/pause + seek slider + volume + time display
- [ ] Implement fullscreen toggle

**Exit criteria:** Can paste a direct video URL and play it with controls.

### Phase 2: YouTube integration

Add yt-dlp and the URL-to-stream pipeline.

- [ ] Bundle or auto-download yt-dlp binary
- [ ] Implement yt-dlp command execution (get-title, video URL, audio URL)
- [ ] Implement YouTube URL detection (watch, youtu.be, shorts)
- [ ] Wire up split audio+video playback via mpv `--audio-file`
- [ ] Implement quality selection (360p-4K) with yt-dlp height filter
- [ ] Build quality selector context menu (HTML)
- [ ] Implement Open URL dialog

**Exit criteria:** Can open a YouTube URL, select quality, and play with audio.

### Phase 3: Playlist and sync

Port the OAuth and playlist API integration.

- [ ] Implement OAuth 2.0 PKCE flow (local HTTP callback listener in Rust)
- [ ] Implement token persistence (tokens.json via serde)
- [ ] Implement playlist API client (reqwest): workspaces, bookmarks CRUD
- [ ] Build playlist panel UI (collapsible, workspace selector, bookmark list)
- [ ] Implement position tracking (10-second save interval)
- [ ] Implement completion tracking (local + remote)
- [ ] Implement duration caching
- [ ] Build bookmark context menu (mark complete, delete)

**Exit criteria:** Full playlist sync parity with WPF app.

### Phase 4: Polish and platform

Finalize UX, drag-drop, keyboard shortcuts, and macOS support.

- [ ] Implement drag-drop (play URL, add to playlist)
- [ ] Implement all keyboard shortcuts (space, F11, Esc, arrow keys)
- [ ] Build dark theme CSS
- [ ] Implement settings persistence (collapsed state, completed set, durations)
- [ ] Login status display and sign-out flow
- [ ] Status overlay text ("Press Ctrl+O...")
- [ ] Test and fix on macOS (mpv embedding, paths, keychain for tokens)
- [ ] Configure Tauri build for Windows (NSIS) and macOS (DMG)
- [ ] App icon and metadata

**Exit criteria:** Feature parity with WPF app, runs on Windows and macOS.

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| mpv window embedding conflicts with WebView | Video doesn't render correctly | Use overlay window approach; fall back to separate window |
| mpv IPC latency causes sluggish controls | Poor UX on seek/volume | IPC is local socket, typically <1ms; buffer UI state optimistically |
| yt-dlp stream URLs expire | Playback fails after ~6 hours | Re-fetch URLs on 403/expiry (same as current app) |
| macOS code signing and notarization | Can't distribute outside App Store | Tauri supports signing via CLI config; budget time for Apple Developer setup |
| Rust learning curve | Slower initial development | Backend is ~500-800 lines; mpv IPC and HTTP calls are well-documented in Rust |
| WebView2 not present on Windows 10 | App won't launch | Tauri auto-installs WebView2 bootstrapper; can also bundle it |

---

## Files and Dependencies to Port

### From WPF (keep logic, rewrite in Rust/TS)

| WPF File | New Location | Language |
|----------|-------------|----------|
| MainWindow.xaml.cs (playback logic) | src-tauri/src/mpv.rs | Rust |
| MainWindow.xaml.cs (UI events) | src/App.svelte or src/main.ts | TypeScript |
| MainWindow.xaml (layout) | src/index.html + src/styles.css | HTML/CSS |
| Services/PlaylistApiService.cs | src-tauri/src/api.rs | Rust |
| Services/PlaylistAuthService.cs | src-tauri/src/auth.rs | Rust |
| Models/AppSettings.cs | src-tauri/src/settings.rs | Rust |
| Models/Bookmark.cs | src-tauri/src/models.rs | Rust |
| Models/TokenStore.cs | src-tauri/src/auth.rs | Rust |
| Models/Workspace.cs | src-tauri/src/models.rs | Rust |
| OpenUrlDialog.xaml | src/components/OpenUrlDialog | HTML/CSS/TS |
| VirtualDesktopPinner.cs | Drop (not cross-platform) | — |

### External dependencies

| Current (WPF) | New (Tauri) |
|---------------|------------|
| LibVLCSharp.WPF + VideoLAN.LibVLC.Windows | mpv binary (sidecar) |
| .NET 8.0 Runtime | None (Rust compiles to native) |
| yt-dlp.exe (auto-downloaded) | yt-dlp binary (sidecar or auto-downloaded) |

### Rust crate dependencies (estimated)

```toml
[dependencies]
tauri = { version = "2", features = ["window-all", "shell-all"] }
serde = { version = "1", features = ["derive"] }
serde_json = "1"
reqwest = { version = "0.12", features = ["json"] }
tokio = { version = "1", features = ["full"] }
dirs = "6"                    # platform app data directories
```
