using System.Windows;
using AsciiVision.App.Views;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Media;
using AsciiVision.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AsciiVision.App.ViewModels;

/// <summary>Console window, recording, export and screenshots.</summary>
public sealed partial class MainViewModel
{
    private bool _consoleBusy;

    // ---------------------------------------------------------------- Console

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenConsoleCommand), nameof(CloseConsoleCommand))]
    private bool _isConsoleOpen;

    [ObservableProperty]
    private string _consoleStatus = "Console closed";

    [RelayCommand(CanExecute = nameof(CanOpenConsole))]
    private async Task OpenConsoleAsync()
    {
        if (DisplayMode == Core.Configuration.DisplayMode.App)
        {
            DisplayMode = Core.Configuration.DisplayMode.Both; // opens the console via the display-mode sync
        }
        else
        {
            await SyncConsoleWithDisplayModeAsync();
        }
    }

    private bool CanOpenConsole() => !IsConsoleOpen;

    [RelayCommand(CanExecute = nameof(IsConsoleOpen))]
    private async Task CloseConsoleAsync()
    {
        if (DisplayMode != Core.Configuration.DisplayMode.App)
        {
            DisplayMode = Core.Configuration.DisplayMode.App; // closes the console via the display-mode sync
        }
        else
        {
            await _console.CloseAsync();
            UpdateConsoleState();
        }
    }

    /// <summary>Keeps the console window in line with the display mode: open for Console/Both, closed for App.</summary>
    private async Task SyncConsoleWithDisplayModeAsync()
    {
        if (_consoleBusy)
        {
            return;
        }

        _consoleBusy = true;
        try
        {
            bool wanted = DisplayMode != Core.Configuration.DisplayMode.App;
            if (wanted && !_console.IsOpen)
            {
                ConsoleStatus = "Opening console…";
                await _console.OpenAsync();
                _pipeline.RequestRefresh(); // show the current frame right away
            }
            else if (!wanted && _console.IsOpen)
            {
                await _console.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console window failed");
            Notify("The console window could not be opened: " + ex.Message, NotificationSeverity.Error);
            DisplayMode = Core.Configuration.DisplayMode.App;
        }
        finally
        {
            _consoleBusy = false;
            UpdateConsoleState();
        }

        // The mode may have changed again while the console was opening/closing.
        if ((DisplayMode != Core.Configuration.DisplayMode.App) != _console.IsOpen)
        {
            await SyncConsoleWithDisplayModeAsync();
        }
    }

    private void UpdateConsoleState()
    {
        IsConsoleOpen = _console.IsOpen;
        ConsoleStatus = _console.IsOpen
            ? $"Console open · {(_console.TerminalSize is { } size ? $"{size.Columns}×{size.Rows} cells" : "size unknown")} · {(_console.SupportsVirtualTerminal == true ? "VT colors" : "legacy colors")}"
            : "Console closed";
    }

    private void OnConsoleClosedByUser()
    {
        UpdateConsoleState();
        if (DisplayMode != Core.Configuration.DisplayMode.App)
        {
            DisplayMode = Core.Configuration.DisplayMode.App;
            Notify("The console window was closed; output continues in the app preview.", NotificationSeverity.Info);
        }
    }

    // ---------------------------------------------------------------- Recording

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand))]
    private bool _isRecording;

    private bool CanStartRecording() => IsActive && !IsRecording;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var path = _dialogs.SaveFile("Save ASCII recording", "MP4 video (*.mp4)|*.mp4", $"ASCII Vision {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4", folder);
        if (path is not null)
        {
            await StartRecordingToAsync(path);
        }
    }

    private async Task StartRecordingToAsync(string path)
    {
        if (LocateFfmpeg() is null)
        {
            return;
        }

        try
        {
            await _recorder.StartAsync(new RecordingOptions(path, RecordingQuality, RecordingFps));
            IsRecording = true;
            _pipeline.RequestRefresh(); // a static screen may not send a new frame soon
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording could not start");
            Notify("Recording could not start: " + ex.Message, NotificationSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private async Task StopRecordingAsync()
    {
        await StopRecordingCoreAsync(notify: true);
    }

    private async Task StopRecordingCoreAsync(bool notify)
    {
        IsRecording = false;
        RecordingResult? result;
        try
        {
            result = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finalizing the recording failed");
            if (notify)
            {
                Notify("The recording could not be finalized: " + ex.Message, NotificationSeverity.Error);
            }

            return;
        }

        if (!notify)
        {
            return;
        }

        if (result is null)
        {
            Notify("Recording stopped before any frame was captured; no file was written.", NotificationSeverity.Warning);
        }
        else
        {
            Notify($"Recording saved ({FormatTime(result.Duration)}, {result.Width}×{result.Height}): {Path.GetFileName(result.OutputPath)}",
                NotificationSeverity.Success, result.OutputPath);
        }
    }

    // ---------------------------------------------------------------- Export

    private bool CanExport() => !string.IsNullOrEmpty(VideoPath);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var ffmpeg = LocateFfmpeg();
        if (ffmpeg is null || VideoPath is null)
        {
            return;
        }

        MediaInfo info;
        try
        {
            info = await MediaProbe.ProbeAsync(ffmpeg, VideoPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is FfmpegException or IOException)
        {
            Notify("The video could not be read: " + ex.Message, NotificationSeverity.Error);
            return;
        }

        var exportVm = new ExportViewModel(_exporter, _settings.Current, info, _dialogs);
        var window = new ExportWindow { DataContext = exportVm, Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (exportVm.Result is { } result)
        {
            Notify($"Export finished: {Path.GetFileName(result.OutputPath)} ({result.Frames} frames, {result.Width}×{result.Height})",
                NotificationSeverity.Success, result.OutputPath);
        }
    }

    // ---------------------------------------------------------------- Screenshots

    [RelayCommand(CanExecute = nameof(HasFrame))]
    private async Task SaveScreenshotAsync()
    {
        var path = _dialogs.SaveFile("Save current frame", "PNG image (*.png)|*.png|Text file (*.txt)|*.txt",
            $"ASCII Vision {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (path is not null)
        {
            await SaveScreenshotToAsync(path);
        }
    }

    private async Task SaveScreenshotToAsync(string path)
    {
        using var frame = _pipeline.AcquireLatestFrame();
        if (frame is null)
        {
            Notify("There is no frame to save yet.", NotificationSeverity.Warning);
            return;
        }

        try
        {
            if (string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                await FrameImageExporter.SaveTextAsync(frame, path);
            }
            else
            {
                uint? background = ColorUtil.TryParseHex(ScreenshotBackgroundHex, out var bg) ? bg : null;
                await _imageExporter.SavePngAsync(frame, _settings.Current, path, background);
            }

            Notify($"Saved {Path.GetFileName(path)}", NotificationSeverity.Success, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCvSharp.OpenCVException)
        {
            Notify("The frame could not be saved: " + ex.Message, NotificationSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFrame))]
    private void CopyText()
    {
        using var frame = _pipeline.AcquireLatestFrame();
        if (frame is null)
        {
            return;
        }

        if (_dialogs.TrySetClipboardText(frame.ToText()))
        {
            Notify($"Copied {frame.Columns}×{frame.Rows} characters to the clipboard.", NotificationSeverity.Success);
        }
        else
        {
            Notify("The clipboard is in use by another application. Try again.", NotificationSeverity.Warning);
        }
    }
}
