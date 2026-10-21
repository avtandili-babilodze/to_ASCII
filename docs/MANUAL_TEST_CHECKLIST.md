# Manual test checklist

Run these against a Release build. FFmpeg must be installed for items 1, 2, 9 and 10. Keep the log open while testing: `%LOCALAPPDATA%\AsciiVision\logs\asciivision-YYYYMMDD.log`.

To get a test video quickly:

```powershell
ffmpeg -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=20" -f lavfi -i "sine=frequency=330:duration=20" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "test clip.mp4"
```

| # | Test | Steps | Expected |
|---|---|---|---|
| 1 | **Load an MP4** | Choose **MP4** and click **Open video…**, or drop a file on the window. Also try a path containing spaces and non-ASCII characters. | File name and info (size, fps, duration, audio) appear. **Play** is enabled. A non-video file shows a readable error. |
| 2 | **Play and pause** | Press **Play**. Use **Pause/Resume** (F8). Drag and click the timeline. Toggle **Loop playback**. | ASCII video plays at the source frame rate and the time readout advances. Pause freezes the frame. Seeking while paused shows the new position. At the end it loops, or stops with "Playback finished". |
| 3 | **Select a monitor** | Choose **Screen**, pick a display (use ⟳ after plugging one in), then **Start Capture**. | Live ASCII of that display. The status bar shows the capture FPS. With **Hide ASCII Vision from capture** on, the app's own window does not appear recursively. |
| 4 | **Select a window** | Choose **Window**, pick from the list or use **Pick with Windows picker…**, then start. Resize the captured window, minimize it, then close it. | Live ASCII of the window. Resizing adapts. Minimizing shows a hint and the capture resumes on restore. Closing it stops the session with "The captured window was closed." |
| 5 | **Switch ASCII styles** | While running, click **Standard / Outline / Hybrid**. Move **Edge sensitivity**, **Edge thickness** and **Hybrid blend**. | Changes apply to the next frame without restarting. Outline draws `\| / \ - _ +` along edges. |
| 6 | **Original colors** | Choose **Original**, then toggle **Tint cell backgrounds**. Try **Custom** with swatches and hex codes, and a light **Background** such as `#F4F1E8`. | Characters take the source colors. Dark colors stay visible. On a light background the ramp flips automatically (dark pixels become dense glyphs). |
| 7 | **Open the console** | Click **Open Console** (this switches the output to **Both**), or choose **Console**. Resize the console window. | A real console window shows the animation without flicker or scrolling. The grid re-fits after resizing. Closing the console window leaves the app running and switches it back to **App**. |
| 8 | **WPF and console together** | Set the output to **Both**. Compare the status bar's Preview and Console FPS. Try the console **grid** options and **color** depths. | Both update from the same capture. The status bar shows both grids when they differ. |
| 9 | **Record an MP4** | While running, **Start Recording**, choose a file, wait about 10 s, then **Stop Recording**. Play the file. | An H.264 MP4 of the ASCII output (not the original image), as long as the real time recorded. Use **Show in folder** to find it. |
| 10 | **Export an imported video** | Load an MP4, set a style, then **Export MP4…**. Choose a resolution, FPS, quality and audio, then **Export**. Run it again and press **Cancel export**. | A progress bar and ETA appear. The finished file contains every frame and the audio stays in sync. Cancelling leaves no output file. The preview stays responsive during the export. |
| 11 | **Save screenshots** | **Save Screenshot…** as `.png`, then as `.txt` (Ctrl+S). Set a **Screenshot background** hex and save another PNG. Use **Copy Text** (Ctrl+Shift+C) and paste into Notepad. | The PNG matches the preview's look (and uses the chosen background). The TXT and clipboard contain the exact characters, one line per row. |
| 12 | **Start and stop repeatedly** | Alternate F5 / Shift+F5 about ten times quickly. Switch source types between runs. | No errors, frozen UI or leaked console processes (check Task Manager). |
| 13 | **Resize windows** | Resize the main window and maximize it; toggle **Fit / 1:1**. Move the window to another monitor with different scaling. | The preview re-fits and stays sharp. There is no stretching beyond the cell aspect, and 1:1 scrolls. |
| 14 | **Close while processing** | While capturing with the console open **and** recording, close the main window. | The title shows "closing…", the recording is finalized (the file plays), the console closes, the process exits, and settings are saved. |
| 15 | Presets | **Save…** a preset, change settings, then **Load…** it. Load a hand-edited preset with out-of-range values, then load an invalid file. | Settings are restored. Out-of-range values are clamped with a warning. An invalid file gets a readable error and the current settings are kept. |
| 16 | Missing FFmpeg | Temporarily rename `ffmpeg.exe` (or point **Settings → FFmpeg** at another `.exe`). Try MP4 and Export. | A clear "FFmpeg was not found…" message. Screen and window capture keep working. |
| 17 | Adaptive quality | Enable **Automatic quality adjustment**, choose **Ultra** and a **Target FPS** of 144 on a slow machine or with a heavy style. | Within a few seconds the grid in the status bar shrinks gradually (never below 40 % or 40 columns) and grows back when there is headroom. It does not oscillate. |
