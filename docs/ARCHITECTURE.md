# Architecture

## Projects and dependencies

```
                    AsciiVision.Core  (net9.0, no UI)
                    ▲      ▲      ▲      ▲
                    │      │      │      │
 AsciiVision.Processing  AsciiVision.Capture  AsciiVision.Rendering (WPF)
       (OpenCV)          (WinRT/D3D11, OpenCV)        ▲
            ▲                                         │
            └──────────── AsciiVision.Export ─────────┘
                                 ▲
                        AsciiVision.App (WPF exe) ── references all of the above
```

There are no circular references. `Core` holds the shared vocabulary:

* `VideoFrame`, `AsciiFrame` and `AsciiFramePool`
* `AsciiSettings`, its validator and the JSON preset serializer
* the interfaces `IFrameSource`, `IAsciiProcessor`, `IAsciiRenderer`, `ITerminalRenderer`, `IRecordingService`, `IVideoExporter`, `ISettingsService` and `ICellGeometryProvider`
* the metrics types
* the FFmpeg locator, probe and process wrapper

The ASCII engine (`Processing`) doesn't depend on WPF or Windows capture, so it can be tested and reused on its own.

Dependency injection (`Microsoft.Extensions.DependencyInjection`) is configured in `App.xaml.cs`. Services are singletons that live as long as the app. Each `IAsciiProcessor` is created through a factory because it isn't thread-safe and every worker needs its own.

## Real-time pipeline

```
 IFrameSource ──OnFrame──► GenerationSink ──► LatestItemMailbox<VideoFrame> (1 slot)
 (capture thread /                │                        │
  decode thread)          drops frames of old              ▼
                          sessions / while paused   FramePipeline loop (dedicated thread)
                                                      • FPS cap (credit scheduler)
                                                      • grid = f(source size, columns, cell aspect)
                                                      • main  = processor.Process(frame, grid)
                                                      • console grid ≠ grid ? second processor
                                                        converts the SAME acquired frame
                                                           │ AsciiFrame (ref-counted, pooled)
                         ┌─────────────────────────────────┼───────────────────────────────┐
                         ▼                                 ▼                               ▼
               PreviewRenderer                     ConsoleRenderer                 RecordingService
           mailbox → glyph raster (own thread)   mailbox → named pipe →           mailbox → compose →
           → UI thread: WritePixels only         console host process (VT)        ffmpeg stdin (CFR)
```

### Frame ownership

* A **`VideoFrame`** is a BGRA32 frame in a buffer rented from `ArrayPool<byte>`. Exactly one owner holds it. Each hand-off transfers ownership. The pipeline keeps the most recent source frame alive so it can re-convert it after a settings change while paused. Disposal returns the buffer to the pool, so a frame is never touched after release.
* A **GPU capture surface** is never handed on. Inside the `FrameArrived` callback the texture is copied to a staging texture, mapped, and copied (or area-downscaled) into a pooled `VideoFrame`. The capture frame and texture are then disposed, before the frame leaves the capture class.
* An **`AsciiFrame`** is reference-counted and recycled through `AsciiFramePool`. Its arrays can pass the 85 KB large-object-heap threshold at Ultra density, so pooling keeps allocation flat at 60 FPS. Every consumer receives its own reference (`Retain()`) and disposes it. The last `Dispose()` returns the frame to the pool. `AcquireLatestFrame()` hands screenshots a retained reference.

### Bounded buffering and latency

Every stage hands work to the next through a single-slot `LatestItemMailbox`. Posting replaces the item that hasn't been taken yet (and disposes it and counts it as dropped). A slow stage therefore sees fewer frames rather than older frames, and latency can't build up. Live sources use this directly.

Video playback paces itself against its own timestamps and skips frames that are more than two frame intervals late.

Offline export does **not** use this path. It has its own deterministic loop with a bounded `Channel` in `Wait` mode, so it never drops a frame.

### Source switching and cancellation

`FramePipeline.StartAsync` is serialized with a semaphore. It stops and disposes the previous source and its loop, then gives the new source a sink tagged with a new **generation number**. Frames that arrive late from the old source's thread are discarded on arrival.

`StopAsync` is idempotent. Sources stop by cancelling their threads or killing their FFmpeg process; killing the process unblocks pipe reads.

Child processes (FFmpeg and the console host) are also placed in a Windows Job Object with *kill on close*. If the app is killed, they die with it.

## ASCII conversion (`AsciiProcessor`)

1. **Cell sampling.** The source is area-averaged down to `columns × rows` (one BGRA pixel per cell). OpenCV's `INTER_AREA` is fast only for integer scale factors, so each axis is halved repeatedly first (exact 2× box filters). Only the small remaining step is resampled. See [PERFORMANCE.md](PERFORMANCE.md).
2. **Tone.** Rec. 709 luma goes through a 256-entry LUT that combines brightness, contrast and gamma, then through a second LUT that maps to an index into the dense→sparse ramp. The ramp direction follows the background color: on a dark background, bright pixels get dense glyphs. The *Invert* option flips it.
3. **Edges (Outline/Hybrid).** A working image of `k × k2` square pixels per cell is built (k ≤ 6, about 450k pixels at most). It is converted to grayscale, Gaussian-blurred, then run through Canny (sensitivity sets the thresholds) and optionally dilated (thickness) and Sobel `gx`/`gy`.
   * The **structure tensor** (`gx²`, `gy²`, `gx·gy`, masked to edge pixels) is box-averaged per cell with an area resize, which takes exact means because the work size is an integer multiple of the grid.
   * Per cell, `φ = ½·atan2(2Sxy, Sxx − Syy)` gives the dominant gradient orientation. Using the tensor avoids the sign cancellation you get on both sides of a thin line. The edge runs perpendicular to the gradient and is binned to `- / | \`.
   * A horizontal edge whose mean vertical position is in the bottom of the cell becomes `_`. Strong edges with low coherence (corners and crossings) become `+`.
   * A cell counts as an edge when it holds roughly a line's worth of edge pixels, `(1.25 − sensitivity) × short cell side`.
4. **Hybrid** uses the edge character where the blend-scaled threshold is met. Elsewhere it uses the shading character, with brightness pulled toward the sparse end so outlines stand out.
5. **Detailed (shape matching).** The source is area-downsampled to 3 × ~6 samples per cell (the rows follow the cell aspect) and converted to luminance, with optional CLAHE applied.
   * For every printable ASCII glyph, `GlyphAtlasCache` measures the mean ink coverage of the same 3 × 6 regions from a 48 px rasterization of the real font. The result is normalized by the single largest region coverage across all glyphs.
   * Each cell's vector is scaled so full brightness maps to the densest glyph (×1.6, so highlights may saturate). It then gets a per-cell contrast boost (`v' = max·(v/max)^e`, with e = 1…3 from *Detail sharpness*), which exaggerates the light/dark split so shapes win over flat averages.
   * The nearest glyph (L2 distance) is found by brute force over the chosen set, using `Vector<float>` SIMD and parallel rows.
   * A bright left edge therefore becomes `[` or `|`, a diagonal boundary `/`, and a bright bottom `_`. Contours get sub-cell precision.
   * Per-region normalization (each region scaled separately) and a 4-column grid were also tried. On a portrait they over-picked letters with ascenders and descenders, and thin vertical glyphs, respectively.
6. **Local contrast.** A CLAHE result (clip limit 2.5, tiles of about 32 px) is blended into the luminance used for character selection: the cell luma in Standard and Hybrid, the Canny input in edge styles, and the sample grid in Detailed.
7. **Color.** Monochrome modes use one foreground color. Original-color mode uses the tone-adjusted mean color of each cell, lifted on dark backgrounds or darkened on light ones so glyphs stay legible. You can also tint each cell's background with a dimmed copy of its color.

All `Mat`s are fields that are reused between frames. The per-cell loop is plain C# over raw pointers and never allocates per pixel or per cell.

### Grid geometry

`rows = round(columns × srcH / srcW / cellAspect)`.

The preview's cell aspect is measured from the chosen font (WPF `GlyphTypeface` advance and line spacing). Consolas is about 2.1. The console can't report its font, so it uses the manual *Character aspect* value.

## Rendering

### Glyph atlas and rasterizer (shared by preview, PNG, recording and export)

`GlyphAtlas` is built once per font, pixel size and character set. All glyphs are drawn white-on-black into one `RenderTargetBitmap` with hinted (`Display`) grayscale text at whole-pixel origins, then cut into per-cell coverage masks.

WPF text objects need an STA thread, so this runs on a dedicated STA dispatcher thread (`WpfGlyphThread`). Any worker can request an atlas, and an LRU cache keeps the few sizes in use. Block characters (`█▓▒░▀▄▌▐`) are generated so they tile without gaps.

`AsciiRasterizer` blends each cell's coverage mask between its foreground and background into a BGRA buffer, with fast paths for blank and solid glyphs. Rows of cells are rendered in parallel. Because every output uses this one function, the preview, screenshots, recordings and exports look identical.

### WPF preview

There is no `TextBlock` per character and no visual-tree churn.

`PreviewRenderer` rasterizes on its own thread. In Fit mode it picks the largest font size that fits the viewport. The finished buffer goes to `AsciiPreviewControl`, which does a single `WriteableBitmap.WritePixels` on the UI thread at `Render` priority. At most one present is queued; newer images replace pending ones.

The bitmap object is reused while its size stays the same, so steady playback causes no layout passes. Its DPI matches the monitor, so 1:1 mode maps one bitmap pixel to one device pixel.

### Console output

Windows terminates the process that owns a console when the user closes that console window. So the console runs in a **separate process**: the same `AsciiVision.exe` started with `--console-host <pipe>`, which calls `AllocConsole`. If the user closes it, only that process ends. The app sees the pipe disconnect, switches the display mode back to App and keeps running.

* **Transport.** The app sends structured cells (characters plus colors) over a duplex named pipe. The host reports its window size whenever it changes, and the pipeline re-fits the console grid to it.
* **VT mode.** The host enables `ENABLE_VIRTUAL_TERMINAL_PROCESSING` and switches to the alternate screen buffer (no scrollback). It hides the cursor and disables Quick Edit, because a stray click would otherwise freeze output.
* **Writing frames.** `VtFrameEncoder` rewrites only the changed span of each row after an absolute cursor move. It emits color codes only when the color changes, as 24-bit, xterm-256 or 16-color codes. There are no screen clears between frames. Output is written with `WriteConsoleW`, so Unicode doesn't depend on the code page.
* **Fallback.** Without VT support, the host writes a whole `CHAR_INFO` buffer with `WriteConsoleOutputW`, using nearest-color 16-color attributes, and makes the screen buffer the same size as the window.
* **Restore.** On exit or stop the host restores console modes, the code page and the cursor, and leaves the alternate screen.

## Capture (`GraphicsCaptureSource`)

Built on the Windows.Graphics.Capture desktop interop, the same approach as Microsoft's WPF ScreenCapture sample, adapted to .NET 9 and CsWinRT:

* **Capture items.** `IGraphicsCaptureItemInterop` comes from `RoGetActivationFactory` and is called through its vtable (`CreateForMonitor`, `CreateForWindow`). The system `GraphicsCapturePicker` is initialized with the main window's HWND.
* **Device and frame pool.** A D3D11 device (hardware, with WARP as fallback) is created with Vortice and wrapped with `CreateDirect3D11DeviceFromDXGIDevice`. The `Direct3D11CaptureFramePool` is free-threaded, so the UI thread is never involved.
* **Texture access.** `IDirect3DDxgiInterfaceAccess` returns the `ID3D11Texture2D` behind each frame.
* **Resizing.** When the content size changes (window resized, display mode changed), the frame pool is recreated at the new size.
* **Item closed.** `GraphicsCaptureItem.Closed` (window closed or display removed) ends the session with a clear message.
* **Device lost.** On `DXGI_ERROR_DEVICE_REMOVED` or `_RESET`, the device and session are recreated on a worker thread, up to 3 times.
* **Teardown.** Objects are detached under the frame lock and disposed outside it, so a `FrameArrived` callback waiting on the lock can never deadlock disposal.
* **Mirror prevention.** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` removes the app's window from captures, so capturing its own monitor doesn't recurse.

## Video files, export and recording

* **Playback (`FfmpegVideoSource`).** It runs `ffmpeg -ss <t> -i file -vf "[HDR tonemap,]fps=F,scale=W:H:flags=area,format=bgra" -f rawvideo pipe:1`, which produces constant-frame-rate raw BGRA. A dedicated thread paces frames against a clock that starts at the first decoded frame, using a high-resolution waitable timer.
  * Pause stops reading, so FFmpeg blocks on the full pipe and uses no CPU.
  * Seek kills FFmpeg and restarts it with input seeking, which is frame-accurate when transcoding. While paused, it shows the frame at the new position.
* **Export (`VideoExporter`).** A decoder process feeds a bounded channel (wait mode, no drops). Each frame is converted, composited at the output size (the font is scaled to fit, with letterboxing in the background color), then written to the encoder process.
  * The encoder command is `-f rawvideo -pix_fmt bgra -video_size WxH -framerate F -i pipe:0 [-i source -map 1:a:0? -c:a aac] -c:v libx264 -crf … -pix_fmt yuv420p -movflags +faststart`.
  * Video starts at t=0 with exactly one frame per 1/F, so the source audio stays in sync.
  * Output goes to a temporary file that is renamed only after FFmpeg exits successfully. Cancelling or a failure leaves nothing behind.
  * stderr is drained continuously into a bounded tail buffer, so FFmpeg can't deadlock on a full pipe. The tail is also used for error messages.
* **Recording (`RecordingService`).** Output frame *n* is written at wall time *n/fps* using the newest ASCII frame available, which gives a constant rate even when capture is idle. If the encoder falls more than 1 s behind, the recorder skips ahead. The size is fixed by the first frame and later frames are fitted into it. Stopping (including when the app closes) closes FFmpeg's stdin so the MP4 is properly finalized.

## Adaptive quality

`AdaptiveQualityController` compares the slowest concurrent stage time (conversion or preview raster) with the frame budget.

* It shrinks density by 15 % after 2 consecutive one-second windows above 85 % of the budget.
* It grows density by 10 % after 4 windows below 50 % of the budget.
* There is a 2 s cooldown after shrinking and a 4 s cooldown before growing.
* The lower limit is 40 % of the requested columns or 40 columns. A grid that starts below 40 columns is never reduced.

Because cost scales with cell count (scale²), one +10 % step from 50 % load lands near 60 %, well inside the hysteresis band, so the controller doesn't oscillate.

## UI (MVVM)

`MainViewModel` (CommunityToolkit.Mvvm source generators) mirrors each setting as a bindable property. Every change publishes a new immutable `AsciiSettings` record through `ISettingsService`. Workers read one consistent snapshot per frame without locks. A settings change also asks the pipeline to re-convert the last frame, so changes show up even while paused or on a static screen.

Worker-thread events (source ended, console closed, recorder failed) are marshalled through the dispatcher.

The window's `Closing` handler runs an async shutdown before the process exits:

1. finalize any recording
2. stop the pipeline
3. close the console
4. save the settings

## Possible future work (not needed for the current targets)

* **GPU conversion.** A D3D11 compute shader could average cells and run Sobel directly on the captured texture, skipping the GPU→CPU copy (~1–2 ms at 1080p) and making 4K capture at 120 Hz practical. It isn't needed now: the full CPU pipeline already takes 0.3–3 ms per frame (see PERFORMANCE.md).
* **SIMD rasterizer.** The glyph blend could use `Vector256<ushort>`. It already takes under 1 ms for a 2160×1216 output.
