using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AsciiVision.App.Services;
using AsciiVision.App.ViewModels;
using AsciiVision.Capture;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Rendering.Preview;

namespace AsciiVision.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly PreviewRenderer _preview;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel, PreviewRenderer preview, PerformanceMetrics metrics)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _preview = preview;
        DataContext = viewModel;

        PreviewControl.Metrics = metrics;
        PreviewControl.ViewportChanged += (_, _) => _preview.Invalidate();
        _preview.Attach(PreviewControl);

        viewModel.OwnerHandleProvider = () => WindowChrome.GetHandle(this);
        viewModel.ExcludeFromCaptureChanged += (_, excluded) => ApplyCaptureExclusion(excluded);

        SourceInitialized += (_, _) =>
        {
            WindowChrome.UseDarkTitleBar(this);
            ApplyCaptureExclusion(viewModel.ExcludeAppFromCapture);
        };
        Drop += OnDrop;
        DragOver += OnDragOver;
        FitToWorkArea();
    }

    /// <summary>Keeps the default size inside the work area on small or highly scaled displays.</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.96);
        Height = Math.Min(Height, area.Height * 0.96);
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
    }

    private void ApplyCaptureExclusion(bool excluded)
    {
        if (IsLoaded || PresentationSource.FromVisual(this) is not null)
        {
            CaptureSupport.SetExcludedFromCapture(WindowChrome.GetHandle(this), excluded);
        }
    }

    private void OnRampPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // One-way display of the matching preset; an explicit pick applies it.
        if (e.AddedItems.Count == 1 && e.AddedItems[0] is Option<string> preset && preset.Value != _viewModel.CharacterRamp)
        {
            _viewModel.SetRampCommand.Execute(preset.Value);
        }
    }

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e) => _viewModel.IsScrubbing = true;

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e) => _viewModel.EndScrub();

    private static string? GetDroppedVideo(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files[0] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedVideo(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedVideo(e) is { } path)
        {
            await _viewModel.LoadVideoAsync(path);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownComplete)
        {
            return;
        }

        // Finish recordings and release capture/console/encoders before the process exits.
        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        IsEnabled = false;
        Title = "ASCII Vision — closing…";
        await _viewModel.ShutdownAsync();
        _preview.Attach(null);
        _shutdownComplete = true;
        Close();
    }
}
