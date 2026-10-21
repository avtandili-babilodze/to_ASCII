using System.Diagnostics;
using System.Globalization;
using AsciiVision.App.Services;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AsciiVision.App.ViewModels;

/// <summary>Offline export of the loaded video with a snapshot of the current ASCII settings.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly IVideoExporter _exporter;
    private readonly AsciiSettings _settings;
    private readonly MediaInfo _input;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ExportViewModel(IVideoExporter exporter, AsciiSettings settings, MediaInfo input, IDialogService dialogs)
    {
        _exporter = exporter;
        _settings = settings;
        _input = input;
        _dialogs = dialogs;
        InputName = Path.GetFileName(input.Path);
        InputDetails = $"{input.Width}×{input.Height} · {input.FrameRate.ToString("0.##", CultureInfo.CurrentCulture)} fps · {MainViewModel.FormatTime(input.Duration)}";
        SettingsSummary = $"{settings.Style} · {settings.ColorMode} colors · {settings.EffectiveColumns} columns · {settings.FontFamily} {settings.FontSize:0.#} px";
        HasAudio = input.HasAudio;
        IncludeAudio = input.HasAudio;
        var folder = Path.GetDirectoryName(input.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        OutputPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(input.Path) + " (ASCII).mp4");

        Resolutions =
        [
            new("Native character raster", null),
            new("1280 × 720 (720p)", (1280, 720)),
            new("1920 × 1080 (1080p)", (1920, 1080)),
            new("2560 × 1440 (1440p)", (2560, 1440)),
            new("3840 × 2160 (4K)", (3840, 2160)),
            new($"Source size ({input.Width} × {input.Height})", (input.Width & ~1, input.Height & ~1)),
        ];
        SelectedResolution = Resolutions[2];
        FrameRates =
        [
            new($"Source ({input.FrameRate.ToString("0.##", CultureInfo.CurrentCulture)} fps)", null),
            new("24 fps", 24),
            new("30 fps", 30),
            new("60 fps", 60),
        ];
        SelectedFrameRate = FrameRates[0];
        Qualities =
        [
            new("High (CRF 18, slower)", EncodingQuality.High),
            new("Balanced (CRF 23)", EncodingQuality.Balanced),
            new("Small file (CRF 28)", EncodingQuality.Small),
        ];
        SelectedQuality = Qualities[0];
    }

    public string InputName { get; }
    public string InputDetails { get; }
    public string SettingsSummary { get; }
    public bool HasAudio { get; }

    public IReadOnlyList<Option<(int Width, int Height)?>> Resolutions { get; }
    public IReadOnlyList<Option<double?>> FrameRates { get; }
    public IReadOnlyList<Option<EncodingQuality>> Qualities { get; }

    [ObservableProperty] private Option<(int Width, int Height)?> _selectedResolution;
    [ObservableProperty] private Option<double?> _selectedFrameRate;
    [ObservableProperty] private Option<EncodingQuality> _selectedQuality;
    [ObservableProperty] private bool _includeAudio;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsNotExporting))]
    private bool _isExporting;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isFinished;

    public bool IsNotExporting => !IsExporting;

    public ExportResult? Result { get; private set; }

    /// <summary>Set when the user chose to cancel and close; the window closes when the export stops.</summary>
    public bool CloseWhenIdle { get; private set; }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void BrowseOutput()
    {
        var path = _dialogs.SaveFile("Export ASCII video", "MP4 video (*.mp4)|*.mp4", Path.GetFileName(OutputPath), Path.GetDirectoryName(OutputPath));
        if (path is not null)
        {
            OutputPath = path;
        }
    }

    private bool CanStart() => !IsExporting && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsExporting = true;
        IsFinished = false;
        Progress = 0;
        StatusText = "Starting…";
        _cts = new CancellationTokenSource();
        var options = new ExportOptions
        {
            InputPath = _input.Path,
            OutputPath = OutputPath,
            Resolution = SelectedResolution.Value,
            FramesPerSecond = SelectedFrameRate.Value,
            Quality = SelectedQuality.Value,
            IncludeAudio = IncludeAudio && HasAudio,
        };

        var progress = new Progress<ExportProgress>(p =>
        {
            Progress = p.Fraction * 100;
            var remaining = p.FramesPerSecond > 0 ? TimeSpan.FromSeconds((p.FramesTotal - p.FramesDone) / p.FramesPerSecond) : TimeSpan.Zero;
            StatusText = $"Frame {p.FramesDone:N0} of ~{p.FramesTotal:N0} · {p.FramesPerSecond:0.0} fps · about {MainViewModel.FormatTime(remaining)} left";
        });

        try
        {
            // Export runs entirely on worker threads; the UI only receives progress reports.
            var result = await Task.Run(() => _exporter.ExportAsync(options, _settings, progress, _cts.Token));
            Result = result;
            Progress = 100;
            IsFinished = true;
            StatusText = $"Done: {result.Frames:N0} frames, {result.Width}×{result.Height}{(result.HasAudio ? ", with audio" : string.Empty)}, in {MainViewModel.FormatTime(result.Elapsed)}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled. No file was written.";
            Progress = 0;
        }
        catch (Exception ex)
        {
            StatusText = "Export failed: " + ex.Message;
            Progress = 0;
        }
        finally
        {
            IsExporting = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Result is not null)
        {
            _dialogs.ShowInExplorer(Result.OutputPath);
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Returns true when the window may close; asks before abandoning a running export.</summary>
    public bool ConfirmClose()
    {
        if (!IsExporting)
        {
            return true;
        }

        if (_dialogs.Confirm("Cancel export?", "An export is in progress. Cancel it and close?"))
        {
            CloseWhenIdle = true;
            _cts?.Cancel();
        }

        return false; // the window closes once the cancellation has completed
    }
}
