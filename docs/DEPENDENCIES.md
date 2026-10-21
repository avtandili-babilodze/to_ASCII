# Dependencies and licenses

The licenses below were read from each package's `.nuspec`. NuGet restores all packages automatically. Versions are pinned centrally in `Directory.Packages.props`.

## Runtime (shipped with the app)

| Component | Version | License | Used for |
|---|---|---|---|
| [OpenCvSharp5](https://github.com/shimat/opencvsharp) | 5.0.0.20261012 | Apache-2.0 | .NET wrapper for OpenCV 5: area resampling, Canny, Sobel, morphology, PNG encoding, drawing the test pattern |
| OpenCvSharp5.runtime.win (OpenCV 5.0.0 native, `OpenCvSharpExtern.dll`) | 5.0.0.20261012 | Apache-2.0 (OpenCV is Apache-2.0) | Native x64 OpenCV build |
| [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) | 8.4.0 | MIT | `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generators |
| Microsoft.Extensions.DependencyInjection | 9.0.20 | MIT | Composition root |
| Microsoft.Extensions.Logging (+ .Abstractions, .Debug) | 9.0.20 | MIT | Logging (file logger in the app, debug output) |
| [Vortice.Direct3D11 / Vortice.DXGI](https://github.com/amerkoleci/Vortice.Windows) | 3.8.2 | MIT | D3D11 device, staging texture and map for capture |
| SharpGen.Runtime (Vortice dependency) | 2.4.2-beta | MIT | COM interop runtime used by Vortice |
| Microsoft.Windows.SDK.NET (CsWinRT projections, from the `net9.0-windows10.0.19041.0` target) | SDK-provided | MIT | Windows.Graphics.Capture and WinRT interop |
| .NET 9 / WPF | 9.0 | MIT | Runtime and UI framework (Fluent dark theme) |

OpenCV's FFmpeg video-I/O plugin (`opencv_videoio_ffmpeg500_64.dll`) ships with the runtime package. It is deliberately **excluded** from the build output, because ASCII Vision never uses `cv::VideoCapture`.

## External tool (not bundled)

| Component | License | Notes |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) (`ffmpeg.exe`, `ffprobe.exe`) | LGPL-2.1+ or GPL-2+/3, depending on the build. Builds that include `libx264`, such as the gyan.dev builds, are GPL. | Runs as a separate process: decodes video (raw BGRA over a pipe) and encodes H.264/AAC MP4. The user installs it; ASCII Vision only finds and runs it. If you redistribute an FFmpeg build next to the app, follow that build's license. |

## Build and test only

| Component | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |

## References consulted

* Windows screen capture: <https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture> and the `Windows.Graphics.Capture` API reference.
* Microsoft WPF ScreenCapture sample (MIT): <https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/dotnet/WPF/ScreenCapture>. The interop GUIDs and call sequence were taken from its `CaptureHelper.cs`. That sample targets .NET Framework and uses `WindowsRuntimeMarshal` and built-in COM casts; this code reimplements the interop for .NET 9 and CsWinRT with raw vtable calls. No sample code was copied wholesale.
* OpenCvSharp README and the OpenCvSharp 4→5 migration guide (namespace and API changes, `Mat.FromPixelData`).
* FFmpeg command-line and formats documentation (rawvideo demuxer and muxer, `-ss` input seeking, `fps`/`scale`/`zscale`/`tonemap` filters, `-movflags +faststart`).
* WPF .NET 9 Fluent theme sources (dotnet/wpf, `Themes/PresentationFramework.Fluent`), read to override the accent brushes correctly.
* CommunityToolkit.Mvvm documentation.
