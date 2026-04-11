# Deploy

.NET 8 WPF application using FlyleafLib for video playback.

## Build

From the `app-flyleaf` directory:

```
dotnet build -c Release
```

Produces the release exe at:

```
D:\Projects\Apps\VideoPlayer\app-flyleaf\bin\Release\net8.0-windows\VideoPlayer.exe
```

Point your Start menu / desktop shortcut at that path.

## FFmpeg

FlyleafLib requires FFmpeg 8.0 shared libraries. They live in the project root at `FFmpeg\` and are automatically copied to `bin\<config>\net8.0-windows\FFmpeg\` on every build (see the `<Content Include="FFmpeg\*.dll">` entry in `VideoPlayer.csproj`).

The `Flyleaf.FFmpeg.Bindings` NuGet package version (8.0.1) must match the FFmpeg DLL version (8.0) — do not upgrade one without the other.

## Run (dev)

```
dotnet run              # Debug
dotnet run -c Release   # Release
```

In Visual Studio / Rider:
- **F5** — build + run with debugger
- **Ctrl+F5** — build + run without debugger
- **Ctrl+Shift+B** — build only
