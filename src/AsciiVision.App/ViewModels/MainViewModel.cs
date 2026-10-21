using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AsciiVision.App.Services;
using AsciiVision.Capture;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Media;
using AsciiVision.Export;
using AsciiVision.Processing;
using AsciiVision.Rendering.Console;
using AsciiVision.Rendering.Glyphs;
using AsciiVision.Rendering.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;

namespace AsciiVision.App.ViewModels;

/// <summary>
/// The main window's state: source selection, session control, live settings, outputs and metrics.
/// All heavy work happens in the pipeline and renderers; this class only orchestrates and marshals
/// worker-thread events onto the UI dispatcher.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly FramePipeline _pipeline;
    private readonly PreviewRenderer _preview;
    private readonly ConsoleRenderer _console;
    private readonly RecordingService _recorder;
    private readonly FrameImageExporter _imageExporter;
    private readonly IVideoExporter _exporter;
    private readonly FfmpegLocator _ffmpegLocator;
    private readonly PerformanceMetrics _metrics;
    private readonly GlyphAtlasCache _glyphs;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _timelineTimer;
    private readonly DispatcherTimer _seekDebounce;

    private GraphicsCaptureItem? _pickedItem;
    private IntPtr _capturedWindowHandle;
    private long _sessionStartTimestamp;
    private bool _updatingPosition;
    private string? _metricsLogPath;

    public MainViewModel(
        SettingsService settings,
        FramePipeline pipeline,
        PreviewRenderer preview,
        ConsoleRenderer console,
        RecordingService recorder,
        FrameImageExporter imageExporter,
        IVideoExporter exporter,
        FfmpegLocator ffmpegLocator,
        PerformanceMetrics metrics,
        GlyphAtlasCache glyphs,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        _settings = settings;
        _pipeline = pipeline;
        _preview = preview;
        _console = console;
        _recorder = recorder;
        _imageExporter = imageExporter;
        _exporter = exporter;
        _ffmpegLocator = ffmpegLocator;
        _metrics = metrics;
        _glyphs = glyphs;
        _dialogs = dialogs;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        LoadFromSettings(settings.Current);

        _pipeline.SourceEnded += (_, e) => _dispatcher.InvokeAsync(() => OnSourceEndedAsync(e));
        _pipeline.ProcessingFailed += (_, ex) => _dispatcher.InvokeAsync(() => OnProcessingFailedAsync(ex));
        _console.Closed += (_, _) => _dispatcher.InvokeAsync(OnConsoleClosedByUser);
        _recorder.Failed += (_, message) => _dispatcher.InvokeAsync(() =>
        {
            IsRecording = false;
            Notify("Recording stopped because encoding failed: " + message, NotificationSeverity.Error);
        });

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), _dispatcher);
        _timelineTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => UpdateTimeline(), _dispatcher);
        _seekDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => CommitSeek(), _dispatcher);
        _statusTimer.Start();

        foreach (var warning in settings.LoadWarnings)
        {
            Notify(warning, NotificationSeverity.Warning);
        }

        RefreshSources();
        UpdateFfmpegStatus();
        _ = LoadFontsAsync();
    }

    /// <summary>Supplies the main window handle (needed by the system capture picker).</summary>
    public Func<IntPtr>? OwnerHandleProvider { get; set; }

    // ---------------------------------------------------------------- Sources

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    public ObservableCollection<WindowInfo> Windows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private SourceKind _selectedSourceKind = SourceKind.Screen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private MonitorInfo? _selectedMonitor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private WindowInfo? _selectedWindow;

    [ObservableProperty]
    private string? _pickedItemName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(VideoFileName))]
    private string? _videoPath;

    public string? VideoFileName => VideoPath is null ? null : Path.GetFileName(VideoPath);

    [ObservableProperty]
    private string? _videoInfoText;

    [ObservableProperty]
    private bool _videoHasAudio;

    [ObservableProperty]
    private bool _loopVideo = true;

    public bool IsCaptureSupported => CaptureSupport.IsCaptureSupported;

    public string CaptureUnsupportedReason => CaptureSupport.UnsupportedReason;

    partial void OnSelectedWindowChanged(WindowInfo? value)
    {
        if (value is not null)
        {
            _pickedItem = null;
            PickedItemName = null;
        }
    }

    partial void OnLoopVideoChanged(bool value)
    {
        if (_pipeline.CurrentSource is IPlaybackControl playback)
        {
            playback.Loop = value;
        }
    }

    [RelayCommand]
    private void RefreshSources()
    {
        var monitor = SelectedMonitor?.DeviceName;
        Monitors.Clear();
        foreach (var m in CaptureTargets.GetMonitors())
        {
            Monitors.Add(m);
        }

        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == monitor) ?? Monitors.FirstOrDefault();

        var window = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var w in CaptureTargets.GetWindows().OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title))
        {
            Windows.Add(w);
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Handle == window);
    }

    [RelayCommand]
    private async Task PickWithSystemPickerAsync()
    {
        try
        {
            var item = await CapturePicker.PickAsync(OwnerHandleProvider?.Invoke() ?? IntPtr.Zero);
            if (item is null)
            {
                return;
            }

            _pickedItem = item;
            SelectedWindow = null;
            PickedItemName = item.DisplayName;
            StartCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture picker failed");
            Notify("The Windows capture picker could not be opened: " + ex.Message, NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task BrowseVideoAsync()
    {
        var path = _dialogs.OpenFile("Open video", "Video files (*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi)|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi|All files (*.*)|*.*",
            VideoPath is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : Path.GetDirectoryName(VideoPath));
        if (path is not null)
        {
            await LoadVideoAsync(path);
        }
    }

    /// <summary>Selects a video file (also used by drag &amp; drop and the command line).</summary>
    public async Task LoadVideoAsync(string path)
    {
        var ffmpeg = LocateFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        try
        {
            var info = await MediaProbe.ProbeAsync(ffmpeg, path, CancellationToken.None);
            VideoPath = path;
            VideoHasAudio = info.HasAudio;
            VideoInfoText = string.Join("  ·  ", new[]
            {
                $"{info.Width}×{info.Height}",
                $"{info.FrameRate.ToString("0.##", CultureInfo.CurrentCulture)} fps",
                FormatTime(info.Duration),
                info.HasAudio ? "audio" : "no audio",
                info.IsHdr ? "HDR → SDR" : null,
            }.Where(s => s is not null));
            SelectedSourceKind = SourceKind.Video;
        }
        catch (Exception ex) when (ex is FfmpegException or FileNotFoundException or IOException or JsonException)
        {
            Notify($"\"{Path.GetFileName(path)}\" could not be opened: {ex.Message}", NotificationSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- Session

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsActive), nameof(IsPaused), nameof(StateText), nameof(PauseResumeLabel), nameof(PauseResumeIcon))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(PauseResumeCommand), nameof(StartRecordingCommand))]
    private SessionState _state = SessionState.Idle;

    [ObservableProperty]
    private string _sourceTitle = "No source";

    [ObservableProperty]
    private bool _isVideoSource;

    [ObservableProperty]
    private string? _waitingMessage;

    public bool IsIdle => State == SessionState.Idle;

    public bool IsActive => State is SessionState.Running or SessionState.Paused;

    public bool IsPaused => State == SessionState.Paused;

    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    public string PauseResumeIcon => IsPaused ? "\uE768" : "\uE769"; // Segoe Fluent Icons: Play / Pause

    public string StateText => State switch
    {
        SessionState.Running => IsVideoSource ? "PLAYING" : "LIVE",
        SessionState.Paused => "PAUSED",
        SessionState.Starting => "STARTING",
        SessionState.Stopping => "STOPPING",
        _ => "IDLE",
    };

    private bool CanStart() => State == SessionState.Idle && SelectedSourceKind switch
    {
        SourceKind.Screen => SelectedMonitor is not null && IsCaptureSupported,
        SourceKind.Window => (SelectedWindow is not null || _pickedItem is not null) && IsCaptureSupported,
        SourceKind.Video => !string.IsNullOrEmpty(VideoPath),
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart())
        {
            return;
        }

        State = SessionState.Starting;
        DismissNotification();
        IFrameSource? source = null;
        try
        {
            source = await CreateSourceAsync();
            if (source is null)
            {
                State = SessionState.Idle;
                return;
            }

            _preview.Clear();
            await _pipeline.StartAsync(source);
            _sessionStartTimestamp = Stopwatch.GetTimestamp();
            SourceTitle = source.DisplayName;
            IsVideoSource = source is IPlaybackControl;
            if (source is FfmpegVideoSource video)
            {
                video.Loop = LoopVideo;
                DurationSeconds = Math.Max(0.1, video.Duration.TotalSeconds);
                PositionSeconds = 0;
                _timelineTimer.Start();
                if (video.IsToneMapping)
                {
                    Notify("HDR video detected: it is tone-mapped to SDR for conversion.", NotificationSeverity.Info);
                }
            }

            State = SessionState.Running;
            OnPropertyChanged(nameof(StateText));
            await SyncConsoleWithDisplayModeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the source failed");
            if (source is not null)
            {
                await source.DisposeAsync();
            }

            State = SessionState.Idle;
            Notify(DescribeStartError(ex), NotificationSeverity.Error);
        }
    }

    private async Task<IFrameSource?> CreateSourceAsync()
    {
        var options = new GraphicsCaptureOptions { CaptureCursor = CaptureCursor };
        switch (SelectedSourceKind)
        {
            case SourceKind.Screen:
                _capturedWindowHandle = IntPtr.Zero;
                return GraphicsCaptureSource.ForMonitor(SelectedMonitor!, options, _logger);
            case SourceKind.Window:
                if (_pickedItem is not null)
                {
                    _capturedWindowHandle = IntPtr.Zero;
                    return GraphicsCaptureSource.ForItem(_pickedItem, SourceKind.Window, options, _logger);
                }

                _capturedWindowHandle = SelectedWindow!.Handle;
                return GraphicsCaptureSource.ForWindow(SelectedWindow, options, _logger);
            case SourceKind.Video:
                var ffmpeg = LocateFfmpeg();
                return ffmpeg is null ? null : await FfmpegVideoSource.OpenAsync(ffmpeg, VideoPath!, _metrics, _logger);
            default:
                return new SyntheticFrameSource(1280, 720, 60);
        }
    }

    private static string DescribeStartError(Exception ex) => ex switch
    {
        CaptureException c => c.Message,
        FfmpegNotFoundException f => f.Message,
        FfmpegException f => "The video could not be played: " + f.Message,
        FileNotFoundException => "The video file no longer exists.",
        UnauthorizedAccessException => "Access was denied. Capture permission may be blocked in Windows privacy settings.",
        DllNotFoundException or TypeInitializationException =>
            "A native component failed to load (OpenCV or graphics). Reinstall ASCII Vision or install the latest Visual C++ runtime.",
        _ => $"Could not start the source ({ex.GetType().Name}). Details were written to the log in {App.LogDirectory}.",
    };

    [RelayCommand(CanExecute = nameof(IsActive))]
    private async Task StopAsync()
    {
        await StopSessionAsync();
    }

    private async Task StopSessionAsync(bool stopRecording = true)
    {
        if (State is SessionState.Idle or SessionState.Stopping)
        {
            return;
        }

        State = SessionState.Stopping;
        _timelineTimer.Stop();
        if (stopRecording && IsRecording)
        {
            await StopRecordingCoreAsync(notify: true);
        }

        await _pipeline.StopAsync();
        WaitingMessage = null;
        State = SessionState.Idle;
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void PauseResume()
    {
        if (State == SessionState.Running)
        {
            _pipeline.Pause();
            State = SessionState.Paused;
        }
        else if (State == SessionState.Paused)
        {
            _pipeline.Resume();
            State = SessionState.Running;
        }
    }

    private async Task OnSourceEndedAsync(SourceEndedEventArgs e)
    {
        var severity = e.Reason switch
        {
            SourceEndReason.Completed => NotificationSeverity.Info,
            SourceEndReason.SourceClosed => NotificationSeverity.Warning,
            _ => NotificationSeverity.Error,
        };
        await StopSessionAsync();
        Notify(e.Message ?? "The source stopped.", severity);
    }

    private async Task OnProcessingFailedAsync(Exception ex)
    {
        await StopSessionAsync();
        Notify("Frame processing failed and the session was stopped: " + ex.Message, NotificationSeverity.Error);
    }

    // ---------------------------------------------------------------- Timeline

    [ObservableProperty]
    private double _durationSeconds = 1;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private string _timeText = "00:00 / 00:00";

    /// <summary>True while the user holds the timeline slider (position updates are suspended).</summary>
    public bool IsScrubbing { get; set; }

    partial void OnPositionSecondsChanged(double value)
    {
        TimeText = $"{FormatTime(TimeSpan.FromSeconds(value))} / {FormatTime(TimeSpan.FromSeconds(DurationSeconds))}";
        if (!_updatingPosition)
        {
            _seekDebounce.Stop();
            _seekDebounce.Start();
        }
    }

    /// <summary>Called when the user releases the timeline: seek immediately.</summary>
    public void EndScrub()
    {
        IsScrubbing = false;
        _seekDebounce.Stop();
        CommitSeek();
    }

    private void CommitSeek()
    {
        _seekDebounce.Stop();
        if (_pipeline.CurrentSource is IPlaybackControl playback)
        {
            playback.Seek(TimeSpan.FromSeconds(PositionSeconds));
        }
    }

    private void UpdateTimeline()
    {
        if (IsScrubbing || _seekDebounce.IsEnabled || _pipeline.CurrentSource is not IPlaybackControl playback)
        {
            return;
        }

        _updatingPosition = true;
        PositionSeconds = playback.Position.TotalSeconds;
        _updatingPosition = false;
    }

    internal static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- Status bar

    [ObservableProperty] private string _captureFps = "—";
    [ObservableProperty] private string _processingFps = "—";
    [ObservableProperty] private string _previewFps = "—";
    [ObservableProperty] private string _consoleFps = "—";
    [ObservableProperty] private string _processingMs = "—";
    [ObservableProperty] private string _gridText = "—";
    [ObservableProperty] private string _droppedText = "0";
    [ObservableProperty] private string _recordingText = "Off";
    [ObservableProperty] private bool _hasFrame;

    private void UpdateStatus()
    {
        var m = _metrics.Snapshot();
        bool active = IsActive;
        CaptureFps = active ? m.CaptureFps.ToString("0", CultureInfo.InvariantCulture) : "—";
        ProcessingFps = active ? m.ProcessingFps.ToString("0", CultureInfo.InvariantCulture) : "—";
        PreviewFps = active ? m.PreviewFps.ToString("0", CultureInfo.InvariantCulture) : "—";
        ConsoleFps = active && IsConsoleOpen ? m.ConsoleFps.ToString("0", CultureInfo.InvariantCulture) : "—";
        ProcessingMs = m.ProcessingMs > 0 ? m.ProcessingMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "—";
        GridText = m.Grid.IsEmpty ? "—" : m.Grid + (m.ConsoleGrid.IsEmpty || m.ConsoleGrid == m.Grid || !IsConsoleOpen ? string.Empty : $"  (console {m.ConsoleGrid})");
        DroppedText = m.DroppedFrames.ToString(CultureInfo.InvariantCulture);
        RecordingText = IsRecording ? "● REC " + FormatTime(_recorder.Elapsed) : "Off";

        bool hasFrame;
        using (var latest = _pipeline.AcquireLatestFrame())
        {
            hasFrame = latest is not null;
        }

        if (hasFrame != HasFrame)
        {
            HasFrame = hasFrame;
            SaveScreenshotCommand.NotifyCanExecuteChanged();
            CopyTextCommand.NotifyCanExecuteChanged();
        }

        UpdateWaitingMessage();
        WriteMetricsLog(m);
    }

    private void UpdateWaitingMessage()
    {
        string? message = null;
        if (State == SessionState.Running)
        {
            if (_capturedWindowHandle != IntPtr.Zero && CaptureTargets.IsWindowAlive(_capturedWindowHandle) && CaptureTargets.IsMinimized(_capturedWindowHandle))
            {
                message = "The captured window is minimized. Windows does not deliver frames for minimized windows — restore it to continue.";
            }
            else if (_pipeline.LastSourceFrameTimestamp == 0 && Stopwatch.GetElapsedTime(_sessionStartTimestamp) > TimeSpan.FromSeconds(2.5))
            {
                message = "Waiting for the first frame… Some protected or exclusive-fullscreen content cannot be captured.";
            }
        }

        WaitingMessage = message;
    }

    private void WriteMetricsLog(MetricsSnapshot m)
    {
        if (_metricsLogPath is null || !IsActive)
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                source = SourceTitle,
                style = Style.ToString(),
                display = DisplayMode.ToString(),
                captureFps = Math.Round(m.CaptureFps, 1),
                processingFps = Math.Round(m.ProcessingFps, 1),
                previewFps = Math.Round(m.PreviewFps, 1),
                consoleFps = Math.Round(m.ConsoleFps, 1),
                processingMs = Math.Round(m.ProcessingMs, 2),
                previewRenderMs = Math.Round(m.PreviewRenderMs, 2),
                dropped = m.DroppedFrames,
                grid = m.Grid.ToString(),
                consoleGrid = m.ConsoleGrid.ToString(),
                recording = IsRecording,
            });
            File.AppendAllText(_metricsLogPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- Notifications

    [ObservableProperty]
    private string? _notificationText;

    [ObservableProperty]
    private NotificationSeverity _notificationSeverity;

    [ObservableProperty]
    private string? _notificationPath;

    private DispatcherTimer? _notificationTimer;

    public void Notify(string message, NotificationSeverity severity, string? path = null)
    {
        NotificationText = message;
        NotificationSeverity = severity;
        NotificationPath = path;
        _notificationTimer?.Stop();
        if (severity is NotificationSeverity.Error or NotificationSeverity.Warning)
        {
            _logger.LogWarning("User notification: {Message}", message);
            return; // problems stay until dismissed
        }

        // Confirmations fade away on their own (longer when they offer "Show in folder").
        _notificationTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _notificationTimer.Interval = TimeSpan.FromSeconds(path is null ? 5 : 10);
        _notificationTimer.Tick -= OnNotificationTimeout;
        _notificationTimer.Tick += OnNotificationTimeout;
        _notificationTimer.Start();
    }

    private void OnNotificationTimeout(object? sender, EventArgs e)
    {
        _notificationTimer?.Stop();
        DismissNotification();
    }

    [RelayCommand]
    private void DismissNotification()
    {
        NotificationText = null;
        NotificationPath = null;
    }

    [RelayCommand]
    private void ShowNotificationPath()
    {
        if (NotificationPath is not null)
        {
            _dialogs.ShowInExplorer(NotificationPath);
        }
    }

    // ---------------------------------------------------------------- FFmpeg

    [ObservableProperty]
    private string _ffmpegStatus = string.Empty;

    [ObservableProperty]
    private bool _ffmpegFound;

    private FfmpegInstallation? LocateFfmpeg(bool notify = true)
    {
        var installation = _ffmpegLocator.Locate(_settings.Current.FfmpegPath);
        if (installation is null && notify)
        {
            Notify(new FfmpegNotFoundException().Message, NotificationSeverity.Error);
        }

        return installation;
    }

    private void UpdateFfmpegStatus()
    {
        var installation = _ffmpegLocator.Locate(_settings.Current.FfmpegPath);
        FfmpegFound = installation is not null;
        FfmpegStatus = installation is null
            ? "Not found — MP4 playback, recording and export are unavailable."
            : $"{installation.Version.Replace("ffmpeg version ", string.Empty, StringComparison.OrdinalIgnoreCase).Split(' ')[0]} · {installation.FfmpegPath}"
              + (installation.Capabilities.CanEncodeH264 ? string.Empty : " · no H.264 encoder!");
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var path = _dialogs.OpenFile("Locate ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe");
        if (path is null)
        {
            return;
        }

        _ffmpegLocator.Invalidate();
        _settings.Update(s => s with { FfmpegPath = path });
        UpdateFfmpegStatus();
        if (!FfmpegFound)
        {
            Notify("That file is not a working ffmpeg executable.", NotificationSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- Fonts

    public ObservableCollection<string> FontFamilies { get; } = new();

    private async Task LoadFontsAsync()
    {
        try
        {
            var families = await Task.Run(() => _glyphs.GetMonospacedFamilies());
            FontFamilies.Clear();
            foreach (var f in families)
            {
                FontFamilies.Add(f);
            }

            if (!FontFamilies.Contains(FontFamily, StringComparer.OrdinalIgnoreCase))
            {
                FontFamilies.Insert(0, FontFamily);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating fonts failed");
            FontFamilies.Add(AsciiSettings.DefaultFontFamily);
        }
    }

    // ---------------------------------------------------------------- Automation & shutdown

    public async Task ApplyCommandLineAsync(CommandLineOptions options)
    {
        _metricsLogPath = options.MetricsLogPath is null ? null : Path.GetFullPath(options.MetricsLogPath);
        if (options.ShowInCapture)
        {
            // Session-only: do not overwrite the user's saved preference.
            OverrideCaptureExclusion(false);
        }

        if (options.Style is { } style)
        {
            Style = style;
        }

        if (options.Color is { } color)
        {
            ColorMode = color;
        }

        if (options.Source is { } source)
        {
            var (kind, argument) = source.Split(':', 2) is [var k, var a] ? (k, a) : (source, null);
            switch (kind.ToLowerInvariant())
            {
                case "test":
                    SelectedSourceKind = SourceKind.TestPattern;
                    break;
                case "screen":
                    SelectedSourceKind = SourceKind.Screen;
                    if (int.TryParse(argument, out int index) && index >= 0 && index < Monitors.Count)
                    {
                        SelectedMonitor = Monitors[index];
                    }

                    break;
                case "window":
                    SelectedSourceKind = SourceKind.Window;
                    SelectedWindow = Windows.FirstOrDefault(w => argument is not null && w.Title.Contains(argument, StringComparison.OrdinalIgnoreCase));
                    break;
                case "video":
                    if (argument is not null)
                    {
                        await LoadVideoAsync(argument);
                    }

                    break;
            }
        }

        if (options.Display is { } display)
        {
            DisplayMode = display;
        }

        if (options.Start)
        {
            await StartAsync();
        }

        if (options.RecordPath is not null && IsActive)
        {
            await StartRecordingToAsync(Path.GetFullPath(options.RecordPath));
        }

        if (options.ExitAfterSeconds is { } seconds)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (options.ScreenshotPath is not null)
                {
                    await SaveScreenshotToAsync(Path.GetFullPath(options.ScreenshotPath));
                }

                Application.Current.MainWindow?.Close();
            };
            timer.Start();
        }
    }

    /// <summary>Finalizes recordings and releases capture, console and encoder resources before exit.</summary>
    public async Task ShutdownAsync()
    {
        _statusTimer.Stop();
        _timelineTimer.Stop();
        try
        {
            if (IsRecording)
            {
                await StopRecordingCoreAsync(notify: false);
            }

            await _pipeline.StopAsync();
            await _console.CloseAsync();
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
        }
    }
}
