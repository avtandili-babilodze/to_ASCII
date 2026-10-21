# Performance

Every number here was measured on one machine with real elapsed time (`Stopwatch`). These are not targets.

**Test machine:** AMD Ryzen 7 6800H (8 cores / 16 threads), 15 GB RAM, Radeon 680M + GeForce RTX 3050 Ti Laptop, 1920×1080 display at 144 Hz, Windows 11 23H2 (build 22631), .NET 9.0, OpenCV 5.0.0 (OpenCvSharp5 5.0.0.20261012), FFmpeg 9.0.2 (gyan.dev essentials).

Your hardware will differ. To reproduce:

* conversion and raster timings: `ASCIIVISION_BENCHMARK=1 dotnet test --filter BenchmarkTests --logger "console;verbosity=detailed"`
* live rates: `AsciiVision.exe … --metrics-log metrics.jsonl`

## Conversion time per frame (`AsciiProcessor`, Original colors)

The source is the synthetic test pattern. Each row is the mean of 60 runs after a 10-run warm-up.

| Source | Style | 80 cols | 120 cols | 160 cols | 240 cols |
|---|---|---|---|---|---|
| 1280×720 | Standard | 0.21 ms | 0.29 ms | 0.31 ms | 0.67 ms |
| 1280×720 | Outline | 1.14 ms | 2.56 ms | 3.06 ms | 2.29 ms |
| 1280×720 | Hybrid | 1.07 ms | 2.16 ms | 2.65 ms | 2.32 ms |
| 1920×1080 | Standard | 0.37 ms | 0.37 ms | 0.50 ms | 0.52 ms |
| 1920×1080 | Outline | 2.36 ms | 2.67 ms | 3.03 ms | 2.59 ms |
| 1920×1080 | Hybrid | 2.09 ms | 2.52 ms | 3.29 ms | 2.99 ms |

Measured again after adding the **Detailed** style and turning on local contrast by default. CLAHE adds about 0.2–0.3 ms to the other styles.

| Source | Style | 80 cols | 120 cols | 160 cols | 240 cols |
|---|---|---|---|---|---|
| 1280×720 | **Detailed** | 0.65 ms | 0.99 ms | 1.24 ms | 2.50 ms |
| 1920×1080 | **Detailed** | 0.71 ms | 1.05 ms | 1.49 ms | 2.49 ms |
| 1920×1080 | Standard (+CLAHE) | 0.52 ms | 0.52 ms | 0.65 ms | 0.85 ms |
| 1920×1080 | Hybrid (+CLAHE) | 2.76 ms | 3.07 ms | 3.78 ms | 3.00 ms |

Live: a 1280×720 30 fps portrait video in Detailed style ran at 30/30/31 FPS (capture/processing/preview), 1.7 ms per frame.

The glyph rasterizer draws 240×64 cells at 16 px (a 2160×1216 BGRA image) in **0.80 ms**.

## Live end-to-end rates (from `--metrics-log`)

| Scenario | Capture | Processing | Preview | Console | Frame time | Dropped |
|---|---|---|---|---|---|---|
| Test pattern 1280×720, Standard, App, 120 cols | 60 | 59–60 | 60 | — | 0.64 ms (3.0 ms before the optimizations below) | 1 (start-up) |
| Test pattern, Hybrid + Original colors, **Both** (preview 120×32 + console 103×29 = two conversions per frame) | 60 | 60–61 | 60–61 | 57–58 | 5.3 ms (12.4 ms before the optimizations below) | 15 in 10 s† |
| Test pattern, Outline, Both | 60 | 60 | 60 | 60 | 5.6 ms | 2 |
| **Monitor capture** 1920×1080 @ 144 Hz, Standard + Original, Both | 82–85 | 60 (target cap) | 60 | 60 | 1.6 ms | 4 |
| **Window capture** (Chrome), Hybrid, App | 73–78 | 60–61 | 60–61 | — | 3.0–3.4 ms | 3 |
| MP4 1280×720 @ 30 fps, Outline + Green, App, while recording | 30 | 30 | 30 | — | 3.3 ms | 0 |
| MP4 1280×720 @ 30 fps, Hybrid + Original, App | 30 | 30 | 30 | — | 3.25 ms | 0 |

† In full-color mode almost every cell needs its own 24-bit escape code, and the console host (conhost) cannot quite display 60 such frames per second. The console mailbox drops the frames it cannot show instead of falling behind.

* Monitor and window capture deliver frames only when the content changes, so the capture rate follows what is on screen, up to the 144 Hz refresh rate.
* "Dropped" counts frames replaced in a mailbox before they were used. It is a few frames during start-up, while the first glyph atlas is built.

### Offline export throughput

* 12 s, 1280×720 @ 30 fps clip → 1920×1080 H.264 (CRF 18, `medium`) with AAC audio, Hybrid + Original colors: **360/360 frames in 3–4 s, about 99 FPS** (≈3× real time).
* Integration test: 2 s 320×180 clip → 640×360 at 20 FPS. Exactly 40 frames are written, none dropped.

### Recording

8 s of live MP4-to-ASCII recording at 30 FPS produced 238 frames (7.93 s, 840×448 H.264). That matches the wall-clock time.

## Optimizations that mattered (measured)

1. **Pyramid area downsampling.** OpenCV `INTER_AREA` uses its fast path (parallel, about 0.1 ms for 1080p→540p) only for integer scale factors. A large non-integer reduction like 1920→120 columns runs single-threaded in **4.9 ms**, and 960→720 still takes **3.1 ms**.
   * Each axis is now halved while it stays at least as large as the target. The residual step below 2× is bilinear for large working images and true area averaging for the small cell grid.
   * Standard 1080p went from 5.1 ms to **0.37 ms**.
2. **One full-resolution pass in edge modes.** Cell colors are taken from the edge working image, which is an exact `k × k2` multiple of the grid, instead of from a second resize of the source.
3. **Capped edge working image (~450k pixels).** Large grids use fewer pixels per cell (down to 2–3). Ultra 240-column Outline at 1080p went from 14.5 ms to **2.6 ms**.
4. **Lookup tables.** Brightness, contrast and gamma, plus the ramp index, are two 256-entry lookups per cell. The test pattern's color field also uses a lookup table: it was limited to 31 FPS by `Math.Sin` per pixel and runs at 60 after the change.
5. **Hinted glyph rasterization.** Display-mode text at whole-pixel origins. Small sizes are crisper and the cost is unchanged.
6. **No per-character WPF elements.** Glyph rasterization for the preview takes 0.2–0.3 ms per frame at typical preview sizes and runs on the renderer's own thread. The UI thread does one `WritePixels` per presented frame.

## Allocation behaviour

* Source frames use `ArrayPool<byte>`.
* ASCII frames are pooled and reference-counted.
* Processor `Mat`s and pyramid levels are reused.
* The console protocol and the VT encoder reuse their buffers.
* Preview pixel buffers are pooled (three in flight at most).

`ProcessorReusesPooledFrames` checks that 50 conversions create a single `AsciiFrame`.

## GPU acceleration: assessment

The CPU pipeline uses at most about 3 ms of a 16.7 ms frame budget at every preset, so a 60 FPS target doesn't need a GPU path.

The remaining fixed cost is the GPU→CPU texture copy and map, about 1–2 ms at 1080p. A D3D11 compute-shader pass for cell averaging and Sobel on the captured texture would remove it and would matter for 4K at high refresh rates. It has not been implemented; [ARCHITECTURE.md](ARCHITECTURE.md) describes it under possible future work.
