# Verification report

What was verified, how, and what was not. Everything below ran on Windows 11 23H2 (build 22631) x64, on the test machine described in [PERFORMANCE.md](PERFORMANCE.md).

## Build

* `dotnet restore AsciiVision.sln`: OK
* `dotnet build AsciiVision.sln -c Release --no-incremental`: **0 warnings, 0 errors**. `WPF0001`, the experimental flag on the .NET 9 Fluent `ThemeMode`, is suppressed on purpose.
* `dotnet publish src\AsciiVision.App\AsciiVision.App.csproj -p:PublishProfile=win-x64`: OK (114 MB). The published executable was launched and exercised.

## Automated tests

`dotnet test AsciiVision.sln -c Release` runs **174 tests: 173 passed, 0 failed, 1 skipped**. The skipped test is the opt-in timing benchmark. The FFmpeg integration tests ran because FFmpeg 9.0.2 is installed.

| Area | Tests |
|---|---|
| Brightness â†’ character, ramp direction, ramp bins, brightness/contrast/gamma | `ToneMappingTests`, `AsciiProcessorTests` (white/black/gradient frames, custom, invalid and inverted ramps, light background) |
| Grid size and aspect correction, presets, terminal fit | `GridCalculatorTests` |
| Edge orientation mapping | `EdgeCharacterMapperTests` (tensor â†’ `\| - / \ _ +`, angle bins), plus OpenCV-drawn vertical, horizontal and both diagonal lines through the full Outline pipeline |
| Hybrid rendering | shading and edges together, the blend changes edge emphasis, sensitivity and thickness have effect |
| Color conversion | original, monochrome and custom modes, background fill, legibility lift, hex parsing, xterm-256 and 16-color quantization |
| Invalid settings | clamping, NaN, unknown enums, invalid ramps, corrupt settings file |
| Preset serialization | round trip, partial presets, malformed JSON, wrong types, unknown format, oversized files, Unicode paths, machine path not exported |
| Frame lifecycle | ref counting, pool reuse and growth, over-release, retain after release, mailbox drop-and-dispose, disposed mailbox |
| Pipeline and cancellation | end-to-end synthetic source; console gets its own grid from the same acquired frame; Match Preview shares one conversion; display-mode routing; prompt idempotent stop; rapid source switching never mixes frames; cancelled or failed start cleans up; source-end event; pause plus refresh on settings change; FPS cap; latest frame available after stop |
| Adaptive quality | sustained overload reduces density, lower limits, recovery without oscillation, hysteresis band |
| Console | VT full redraw, no output for an identical frame, minimal changed-span updates, color-change-only codes, 256/16-color codes, cropping without wrap or scroll, control-character sanitizing, binary protocol round trip and truncation |
| Detailed style and local contrast | glyph shape vectors (where the ink is in `_`, `^` and `\|`), normalization and subsets; black â†’ spaces and white â†’ densest glyph; ink follows the bright side of the cell (left/right/top/bottom); only the chosen character set is used; sharpness changes the output; source hue is kept; fallback without shapes; CLAHE reveals detail in a low-contrast image; the fine ramp is valid; a frame-time bound |
| Glyph raster | font metrics, fallback font, atlas contents including generated block glyphs, antialiased glyph pixels in cell colors, clipping, fit-size math |
| FFmpeg integration | probe; real-time playback order and timing; seek while paused then resume; HDR10 (PQ) â†’ SDR tone mapping; export to 640Ã—360 at 20 fps with AAC (exactly 40/40 frames); native-size export; cancelled export leaves no file and no partial file; missing input; constant-rate live recording; empty recording writes nothing; PNG and TXT screenshots in a Unicode folder |

## Runtime tests of the real application

The app was driven from the command line and with Windows UI Automation (scripts in the session scratch area). Metrics come from `--metrics-log`; screenshots were captured and inspected.

| # | Scenario | Result |
|---|---|---|
| 1 | Test pattern, App mode | 60/60/60 FPS capture/processing/preview, UI rendered correctly (dark theme, all panels) |
| 2 | **Monitor capture** (Windows.Graphics.Capture, 1920Ã—1080 at 144 Hz) with **Both** outputs, Original colors | capture 82â€“85 FPS, processing, preview and console 60 FPS, 1.6 ms per frame. A real console window showed 24-bit color output. |
| 3 | **Window capture** (a Chrome window, chosen from the window list) | capture 73â€“78 FPS, processing 60 FPS, Hybrid output correct |
| 4 | **MP4 playback** (path with spaces and `Ã¼`), Outline + Green, while **recording** | 30/30/30 FPS, 0 dropped; timeline and REC indicator shown; recording finalized as valid H.264 840Ã—448, 30 fps, 238 frames â‰ˆ 7.9 s; PNG screenshot saved on exit |
| 5 | Repeated **start/stop Ã—5** via the toolbar buttons (UI Automation) | buttons enable and disable correctly every time |
| 6 | Switching styles, colors, Fit/1:1 and pause/resume while running; changing style while paused | no errors; the paused frame re-renders |
| 7 | **Open Console**, then close the console window like a user (WM_CLOSE) | console process ends, the app stays alive, "Open Console" is re-enabled, output falls back to App |
| 8 | **Export MP4â€¦** dialog, run through the UI | 1920Ã—1080 H.264, 30 fps, 360/360 frames, AAC audio, 12.000 s, about 99 FPS export throughput; progress, cancel and done states displayed |
| 9 | Close the main window (UI Automation `WindowPattern.Close`) | async shutdown, exit code 0, no leftover processes |
| 10 | Hard-kill the app while the console and FFmpeg are running | Job Object ended the console host and FFmpeg too (0 leftover processes) |
| 11 | Resize the console window | console grid re-fit live: 103Ã—29 â†’ 67Ã—19 â†’ 145Ã—41 |
| 12 | **Copy Text** button | clipboard held 32 lines of 120 characters |
| 13 | Published (`artifacts\publish\win-x64`) build | runs, Outline + Both at 60/60/60/60 FPS |

## Image quality evaluation (Detailed style)

A public-domain portrait (Mona Lisa, Wikimedia Commons, cropped to 1280Ã—720) was converted with the real processor and rasterizer at 120 and 160 columns, and the results were inspected visually.

* Standard style showed the face as an undifferentiated blob.
* Detailed style plus local contrast shows the eyes, brows, nose, lips, hair contour, neckline and background hills.

The default parameters (symbols-only set, global normalization, Ã—1.6 brightness gain, 3 Ã— 6 sampling, sharpness 0.5 and local contrast 0.4) were chosen from these side-by-side renders.

## Bugs found by runtime testing (and fixed)

* Creating the WinRT Direct3D device through the Vortice wrapper failed (`InvalidCastException`) because its generic overload marshals the result as a COM object. Now it P/Invokes `d3d11!CreateDirect3D11DeviceFromDXGIDevice` directly.
* Overriding the `SystemColors.Accent*` keys broke the .NET 9 Fluent theme ("'AccentColorLight2' is not a valid value"). Now the named Fluent accent brushes are overridden instead.
* The default window could be taller than a 125 %-scaled 1080p work area. The window is now clamped to the work area.
* The test pattern was limited to 31 FPS by per-pixel `Math.Sin`. It now uses a lookup table and reaches 60 FPS.
* The console host's own window appeared in the capture window list, which allowed a feedback loop. It is now excluded.

## Not verified on hardware (handled in code, but not exercised)

* GPU device loss or driver reset during capture (`DXGI_ERROR_DEVICE_REMOVED` â†’ recreate up to 3 times).
* Changing display resolution or orientation mid-capture. The same frame-pool-recreate path as window resizing handles it, and window resizing is exercised by the content-size logic.
* Multi-monitor selection: only one monitor was attached, though monitor enumeration ran.
* Displays wider than 1920 px (4K downscale-on-copy path), HDR displays, per-monitor DPI changes when moving the window between monitors.
* Minimized-window and protected-content capture. These are expected to deliver no frames, and the app then shows a hint.
* The Windows **capture picker** dialog (it needs a human to click). Only window-list selection was automated.
* The legacy (non-VT) console fallback. Windows 11 conhost always supports VT.
* Windows 10 (1903â€“21H2). Only Windows 11 23H2 was available.
