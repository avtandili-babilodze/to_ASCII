# ASCII Vision

ASCII Vision is a native Windows desktop app that turns moving video into real-time ASCII art.
It can capture a whole monitor, a single application or game window, or play a local MP4 file.
It converts every frame into character art and shows the result in the app, in a real Windows
console window, or in both at once. You can record the live ASCII output or export an imported
video as an ASCII MP4.

It is built with C# / .NET 9, WPF (MVVM), Windows.Graphics.Capture + Direct3D 11, OpenCvSharp5 (OpenCV 5)
and FFmpeg. Everything runs locally. No account, API key or network connection is needed.

```
   ___   _____ _____ _____ _____
  / _ \ /  ___/  __ \_   _|_   _|
 / /_\ \\ `--.| /  \/ | |   | |
 |  _  | `--. \ |     | |   | |
 | | | |/\__/ / \__/\_| |_ _| |_
 \_| |_/\____/ \____/\___/ \___/  VISION
```

## Install (for users)

**Option A: from this repository (the simplest).**

1. Click **Code → Download ZIP** on GitHub and extract it, or clone it with `git clone https://github.com/avtandili-babilodze/to_ASCII.git`.
2. Double-click **`Install.cmd`** in the main folder.
3. Choose where to install. An `ASCII Vision` folder is created inside the folder you pick, and the default is `%LOCALAPPDATA%\Programs`.
4. Start **ASCII Vision** from the desktop or Start menu shortcut it creates.

If a matching GitHub Release exists, setup downloads that ready-made build (about 42 MB) and checks its checksum. Otherwise it **builds the app from the source code**. When no .NET 9 SDK is installed, setup installs one for your user account first (no administrator rights needed, about 250 MB), and the first build takes a few minutes. Source builds include their own .NET runtime.

**Option B: from a release.** Download `AsciiVision-<version>-win-x64.zip` from the [Releases](../../releases) page, extract it, and double-click `Install.cmd` inside.

If Windows shows "Windows protected your PC", click **More info → Run anyway**. The files are not code-signed.

Both ways check what is already on the PC and install only what is missing:

| Requirement | If missing |
|---|---|
| Windows 10 1903+ / Windows 11, 64-bit | setup stops with an explanation |
| .NET 9 Desktop Runtime (x64), release builds only | installed from Microsoft (winget, or the official `aka.ms` download) |
| Visual C++ 2015–2022 Redistributable x64 (≥ 14.30) | installed from Microsoft |
| FFmpeg with H.264 support | a full build from gyan.dev is downloaded into the app folder (`PATH` is not changed) |

Installing the .NET or Visual C++ runtime shows a Windows (UAC) permission prompt. The app itself installs for the current user and appears in **Settings → Apps**, where you can uninstall it. Uninstalling leaves the shared runtimes installed. Reinstalling into a different folder removes the old copy and keeps your settings.

Setup options (PowerShell, from the main folder):

* `.\installer\setup.ps1 -InstallDir "D:\Apps\ASCII Vision"` skips the folder picker.
* `-FromSource` always builds from source.
* `-NoDesktopShortcut` skips the desktop shortcut.
* `-NoLaunch` does not start the app at the end.

## Features

| Area | What you get |
|---|---|
| **Sources** | Monitor capture (multi-monitor), window capture (window list or the Windows capture picker), MP4/video files (play, pause, seek, loop, timeline), built-in animated test pattern |
| **ASCII styles** | **Detailed** (default): shape matching, where the light and dark *inside* each cell picks the glyph whose shape fits, for precise contours and readable faces. **Standard**: brightness → character ramp, with classic, 70-level and block presets. **Outline**: Canny + Sobel structure tensor → `\| / \ - _ +`. **Hybrid**: shading plus outlines, with an adjustable blend. |
| **Color** | White on black, green terminal, original source colors (optional tinted cell backgrounds), custom foreground, custom background |
| **Outputs** | App preview, real console window, or both. Switch at any time without restarting. One capture and conversion feeds every output. |
| **Console** | Separate console process, 24-bit / 256 / 16-color VT output with row-diffing (no flicker, no scrollback), legacy console API fallback, follows the terminal size |
| **Export** | Offline ASCII MP4 export (H.264, resolution / FPS / quality options, source audio, progress, cancel). Live recording of the ASCII output to MP4 |
| **Frames** | Save the current frame as PNG (same look as the preview, optional background color) or TXT. Copy plain text to the clipboard |
| **Tuning** | Grid width and density presets (80/120/160/240/custom), character aspect, font and size, brightness, contrast, gamma, **local contrast (CLAHE)**, detail sharpness, character set, custom ramp, edge sensitivity and thickness, hybrid blend, target FPS, automatic quality adjustment. All settings apply live. |
| **Presets** | Save and load JSON presets with validation. Settings persist between runs |
| **Diagnostics** | Measured capture, processing, preview and console FPS, frame time, grid size, dropped frames, recording status. Log files are written to disk |

## Requirements

* **Windows 10 version 1903 (build 18362) or newer, or Windows 11, x64.**
  * Hiding the app from its own capture (no "infinite mirror") and toggling cursor capture need Windows 10 2004 (build 19041).
* **To build:** the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). NuGet restores everything else: OpenCvSharp5 with its native x64 runtime, CommunityToolkit.Mvvm, Microsoft.Extensions.* and Vortice.Direct3D11.
* **To run a framework-dependent build:** the .NET 9 **Desktop** Runtime (x64). The self-contained publish profile needs nothing.
* **FFmpeg** (`ffmpeg.exe` + `ffprobe.exe`) for MP4 playback, recording and export. Screen and window capture work without it.
  * Install it with `winget install Gyan.FFmpeg`, or download a build from <https://ffmpeg.org/download.html>.
  * ASCII Vision looks for FFmpeg in this order: the path chosen in **Settings → FFmpeg**, the app folder (`ffmpeg.exe` or `ffmpeg\bin\ffmpeg.exe`), `PATH`, then the common winget, Chocolatey, Scoop and `C:\ffmpeg\bin` locations.
  * H.264 export needs an FFmpeg build that includes `libx264` (preferred) or `h264_mf`. HDR tone mapping uses `zscale` + `tonemap` when they are available.
* OpenCV's native library needs the Microsoft Visual C++ 2015–2022 x64 runtime (present on almost all systems). On Windows N or Server editions it also needs Media Foundation.

## Build, test and run

```powershell
dotnet restore AsciiVision.sln
dotnet build AsciiVision.sln -c Release
dotnet test  AsciiVision.sln -c Release

# launch
.\src\AsciiVision.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\AsciiVision.exe
# or
dotnet run --project src\AsciiVision.App -c Release
```

To publish an x64 Release build:

```powershell
# framework-dependent (needs the .NET 9 Desktop Runtime), output in artifacts\publish\win-x64
dotnet publish src\AsciiVision.App\AsciiVision.App.csproj -p:PublishProfile=win-x64

# self-contained (no runtime needed), output in artifacts\publish\win-x64-selfcontained
dotnet publish src\AsciiVision.App\AsciiVision.App.csproj -p:PublishProfile=win-x64-selfcontained
```

### Building a release package

```powershell
.\build-release.ps1            # runs the tests, publishes, then packages
.\build-release.ps1 -SkipTests
```

This creates:

* `release\AsciiVision-<version>-win-x64\`: the ready-to-run installer folder (`Install.cmd`, `install.ps1`, `uninstall.ps1`, `README.txt`, `app\`)
* `release\AsciiVision-<version>-win-x64.zip` and `.zip.sha256`

To publish on GitHub, create a release (for example tag `v1.0.0`) and upload the `.zip` and `.sha256` files as release assets. `release/` and `artifacts/` are git-ignored, so binaries never go into the repository itself. The version comes from `<Version>` in `Directory.Build.props`.

You can open `AsciiVision.sln` in Visual Studio 2022 (17.12 or newer) and press F5. The startup project is `AsciiVision.App`.

## Using ASCII Vision

1. Pick a **source** in the left panel: **Screen** (choose a display), **Window** (choose from the list or use **Pick with Windows picker…**), **MP4** (click **Open video…**, or drop a file onto the window) or **Test**.
2. Pick an **ASCII style** (Standard, Outline or Hybrid) and a **color** mode.
3. Press **Start Capture** (or **Play**, or F5). The center area shows the live ASCII preview.
4. Choose an **output**: **App**, **Console** or **Both**. The console opens as its own window. If you close it, the app keeps running and switches back to App.
5. Adjust anything on the right while the video plays.
6. Use **Start / Stop Recording** to record the ASCII output, **Export MP4…** to convert the loaded video offline, and **Save Screenshot…** or **Copy Text** to keep a single frame.

| Shortcut | Action |
|---|---|
| F5 / Shift+F5 | Start / stop |
| F8 | Pause / resume |
| Ctrl+S | Save screenshot (PNG or TXT) |
| Ctrl+Shift+C | Copy the current frame as text |

### Getting the most detail

* Use the **Detailed** style. It is the default and is also among the cheapest (about 1–2.5 ms per frame).
* More **columns** means more detail: try *Quality (160)* or *Ultra (240)*. Maximize the window so each character still gets a few pixels, or use **1:1** and scroll.
* **Local contrast** (0.3–0.6) brings out eyes, shadows and background texture. **Detail sharpness** trades smooth shading (low) for crisp contours (high).
* **Characters → Symbols** gives the cleanest look. *Symbols + letters* adds more in-between tones.
* With **Original** colors, **Tint cell backgrounds** comes closest to the source picture.

### Preview modes

* **Fit** fills the preview area. The glyphs are rasterized at the largest font size that fits, so the text stays sharp instead of being scaled up.
* **1:1** renders at the configured **Font size**, one bitmap pixel per screen pixel. It scrolls if the image is larger than the area.

The **Font size** also sets the size of screenshots, recordings and native-resolution exports.

### Command line

The command line is optional. It is useful for demos and unattended smoke tests:

```text
AsciiVision.exe [--source test|screen[:N]|window:<title text>|video:<path>] [--start]
                [--display app|console|both] [--style detailed|standard|outline|hybrid]
                [--color white|green|original|custom] [--record <file.mp4>]
                [--screenshot <file.png|.txt>] [--exit-after <seconds>]
                [--metrics-log <file.jsonl>] [--show-in-capture]
```

`--metrics-log` appends the measured status-bar values as one JSON line every 0.5 s. `--show-in-capture` turns off "hide from capture" for that session only, so you can screen-record the app.
`--style` and `--color` behave like clicking the buttons, so they are saved with the settings.

### Files

| What | Where |
|---|---|
| Settings (auto-saved on exit) | `%APPDATA%\AsciiVision\settings.json` |
| Logs | `%LOCALAPPDATA%\AsciiVision\logs\asciivision-YYYYMMDD.log` |
| Presets (default folder) | `Documents\ASCII Vision\Presets` |

## Troubleshooting

* **"FFmpeg was not found"**: install FFmpeg (see above) or point **Settings → FFmpeg → Browse…** at `ffmpeg.exe`.
* **The preview shows itself repeatedly when you capture the screen ASCII Vision is on**: enable **Hide ASCII Vision from capture**. It is on by default and needs Windows 10 2004 or newer. While it is on, other screen recorders and screen sharing also cannot see the window.
* **"Waiting for the first frame…"**: some content can't be captured, such as DRM-protected video and some exclusive-fullscreen games. Windows also sends no frames for minimized windows.
* **The window cannot be captured**: windows of elevated (administrator) processes can't be captured from a non-elevated app.
* **Text looks wrong in the console**: use a TrueType console font such as Cascadia Mono or Consolas. On very old consoles without VT support, the 16-color fallback is used automatically.
* For anything else, read the log file. Errors are logged with full details. The UI only shows a short message.

## Known limitations

* Live recording is video-only. Audio is not captured, by design. MP4 export keeps the source audio.
* Captured frames are 8-bit SDR. When you capture an HDR display, Windows does the HDR→SDR conversion, and colors may look washed out. HDR *video files* (PQ and HLG) are tone-mapped to SDR with FFmpeg's `zscale`/`tonemap` filters when the FFmpeg build has them.
* Minimized windows, protected (DRM) content and some exclusive-fullscreen games can't be captured. The app says so instead of showing black frames.
* Monitor capture shows a yellow capture border on Windows versions that require it.
* The console can't report its font's cell shape, so console grids use the manual **Character aspect** value (2.0 suits most fonts).
* Display content larger than 1920 pixels wide (for example 4K) is downscaled while it is copied off the GPU. Conversion never needs more detail than that.
* Device-lost recovery (a GPU driver reset during capture) and display-mode changes during capture are handled in code but were not exercised on hardware. See [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Project layout

```
AsciiVision.sln
src/
  AsciiVision.Core         models, settings, interfaces, metrics, FFmpeg helpers (no UI)
  AsciiVision.Processing   OpenCV ASCII conversion, edge analysis, frame pipeline, adaptive quality
  AsciiVision.Capture      Windows.Graphics.Capture (monitor/window), FFmpeg playback, test pattern
  AsciiVision.Rendering    glyph atlas + rasterizer, WPF preview control, console host/renderer
  AsciiVision.Export       MP4 encoder, offline exporter, live recorder, PNG/TXT export
  AsciiVision.App          WPF application (XAML views, view models, DI composition)
tests/
  AsciiVision.Tests        unit + integration tests (xUnit)
docs/                      architecture, performance, verification, dependencies, test checklist
Install.cmd                double-click setup from a clone or source ZIP (runs installer/setup.ps1)
installer/                 setup.ps1 (release download or source build), install.ps1 / uninstall.ps1 / Install.cmd for the release zip
build-release.ps1          builds release\AsciiVision-<version>-win-x64(.zip)
```

More documentation:

* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the pipeline, threads, frame ownership and rendering work
* [docs/PERFORMANCE.md](docs/PERFORMANCE.md): measured numbers and the optimizations behind them
* [docs/VERIFICATION.md](docs/VERIFICATION.md): what was compiled, unit-tested and runtime-tested, and how
* [docs/MANUAL_TEST_CHECKLIST.md](docs/MANUAL_TEST_CHECKLIST.md): step-by-step manual tests
* [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md): third-party components and licenses

## Tests

`dotnet test AsciiVision.sln -c Release` runs the unit and integration tests. They cover:

* brightness-to-character conversion and custom ramps
* grid and aspect-ratio math
* edge-orientation mapping, including synthetic line images in Outline mode
* hybrid blending and color modes
* invalid settings and preset (de)serialization
* the frame and pool lifecycle
* cancellation, source switching, pause and refresh in the pipeline
* VT encoding and the console protocol
* glyph rasterization

When FFmpeg is installed, integration tests also cover:

* probing, playback timing, seek and pause
* HDR tone mapping
* full MP4 export with audio, and cancellation that leaves no file behind
* live recording
* PNG and TXT screenshots

These generate their own test media with FFmpeg's `lavfi` sources. Without FFmpeg they are skipped.

To run the timing benchmarks, set `ASCIIVISION_BENCHMARK=1` and run `dotnet test --filter BenchmarkTests --logger "console;verbosity=detailed"`.
