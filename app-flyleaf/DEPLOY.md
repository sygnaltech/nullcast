# Deploy

.NET 8 WPF application using FlyleafLib for video playback.

## Shipping a release

The shippable artifact is a single self-contained installer built by
[`installer/build.ps1`](../installer/build.ps1):

```powershell
.\installer\build.ps1
```

That publishes the app self-contained for `win-x64` and compiles
[`installer/nullcast.iss`](../installer/nullcast.iss) with Inno Setup, producing:

```
installer\Output\nullcast-setup-<version>.exe
```

Roughly 86 MB from a ~300 MB payload. Attach that one file to the GitHub release.

Requirements on the build machine: the .NET SDK and
[Inno Setup](https://jrsoftware.org/isdl.php) 6 or 7 (the script probes the usual install
paths; it does not need to be on `PATH`).

Useful flags:

| Flag | Effect |
| --- | --- |
| `-SkipPublish` | Reuse the existing publish tree and only recompile the installer — for iterating on the `.iss`. |
| `-Configuration Debug` | Publish a Debug build instead of Release. |

### Version

The installer version comes from `<Version>` in `VideoPlayer.csproj` and nowhere else.
Bump it there, or with `version.ps1`:

```powershell
.\version.ps1              # print current
.\version.ps1 0.2.52       # set
```

The `AppId` GUID in `nullcast.iss` must never change — it is how Windows matches an
upgrade to an existing install. A new GUID installs a second copy alongside the old one.

### What the installer does

- **Self-contained** — the .NET 8 runtime is bundled, so target machines need no
  prerequisite. FFmpeg 8.0, the Instrument Sans fonts and `telemetry.json` ship with it.
- **Dual scope** — the wizard asks "for all users" (elevates to `Program Files`) or
  "for me only" (`%LocalAppData%\Programs\Nullcast`, no UAC prompt). Silent installs
  default to per-user; pass `/ALLUSERS` for per-machine.
- **Upgrades in place** — reinstalling over an existing copy replaces it and preserves
  all user data.
- **Uninstall keeps user data by default.** Settings, history, queue, playlists and
  service logins in `%AppData%\VideoPlayer` survive; the uninstaller offers to delete
  them, and that prompt defaults to *No*.

### Signing

The installer is **unsigned**, so SmartScreen shows a "Windows protected your PC" warning
on first run until the download builds reputation. Signing needs an Authenticode
certificate and a `SignTool` line in the `[Setup]` section — neither exists yet.

### Silent install

```powershell
nullcast-setup-0.2.51.exe /VERYSILENT /NORESTART              # per-user
nullcast-setup-0.2.51.exe /VERYSILENT /NORESTART /ALLUSERS    # per-machine (needs admin)
nullcast-setup-0.2.51.exe /VERYSILENT /DIR="D:\Apps\Nullcast" # custom location
```

## Where things live at runtime

| Path | Contents |
| --- | --- |
| `%AppData%\VideoPlayer\` | `settings.json`, `services.json`, `api.json`, history, queue, playlists |
| `%LocalAppData%\Nullcast\tools\` | `yt-dlp.exe` + `deno.exe`, **only when the app directory is read-only** |
| `%Temp%\videoplayer-debug.log` | Debug/crash log |

### yt-dlp and Deno

Neither is bundled — together they are ~110 MB and yt-dlp needs to stay current. Both are
downloaded on first run, and yt-dlp self-updates via `-U` when its local copy is more than
30 days old.

Because yt-dlp rewrites its own binary, it needs a **writable** directory. The app uses its
own install directory when that is writable (dev builds out of `bin\`, and per-user
installs) and otherwise falls back to `%LocalAppData%\Nullcast\tools`, seeding it from any
copies found next to the exe. Without that fallback a `Program Files` install would fail
its updates silently and YouTube extraction would break roughly a month after install —
see `ResolveToolsDirectory` in `MainWindow.xaml.cs`.

## Build (no installer)

From the `app-flyleaf` directory:

```
dotnet build -c Release
```

Produces the release exe at `bin\Release\net8.0-windows\VideoPlayer.exe`.

## FFmpeg

FlyleafLib requires FFmpeg 8.0 shared libraries. They live in the project root at `FFmpeg\`
and are automatically copied to the output on every build (see the
`<Content Include="FFmpeg\*.dll">` entry in `VideoPlayer.csproj`).

The `Flyleaf.FFmpeg.Bindings` NuGet package version (8.0.1) must match the FFmpeg DLL
version (8.0) — do not upgrade one without the other.

## Run (dev)

```
dotnet run              # Debug
dotnet run -c Release   # Release
```

In Visual Studio / Rider:
- **F5** — build + run with debugger
- **Ctrl+F5** — build + run without debugger
- **Ctrl+Shift+B** — build only
