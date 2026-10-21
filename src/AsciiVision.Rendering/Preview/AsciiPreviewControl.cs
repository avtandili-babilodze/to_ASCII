using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;

namespace AsciiVision.Rendering.Preview;

/// <summary>
/// Displays the preview bitmap. A single <see cref="WriteableBitmap"/> is reused while the image
/// size is unchanged, so steady-state frames cause no layout pass, just a pixel upload.
/// The bitmap's DPI matches the monitor so 1:1 mode maps one bitmap pixel to one device pixel.
/// </summary>
public sealed class AsciiPreviewControl : Border, IPreviewSurface
{
    public static readonly DependencyProperty FitModeProperty = DependencyProperty.Register(
        nameof(FitMode), typeof(PreviewFit), typeof(AsciiPreviewControl),
        new PropertyMetadata(PreviewFit.Fit, (d, _) => ((AsciiPreviewControl)d).ApplyFitMode()));

    private static readonly DependencyPropertyKey HasContentPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasContent), typeof(bool), typeof(AsciiPreviewControl), new PropertyMetadata(false));

    public static readonly DependencyProperty HasContentProperty = HasContentPropertyKey.DependencyProperty;

    private readonly ScrollViewer _scroll;
    private readonly Image _image;
    private PreviewBuffer? _pending;
    private int _presentScheduled;
    private WriteableBitmap? _bitmap;
    private volatile int _viewWidth;
    private volatile int _viewHeight;
    private double _dpiScale = 1.0;

    public AsciiPreviewControl()
    {
        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        _scroll = new ScrollViewer
        {
            Content = _image,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Child = _scroll;
        UseLayoutRounding = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => UpdateViewport();
        Loaded += (_, _) => UpdateViewport();
        ApplyFitMode();
    }

    public PreviewFit FitMode
    {
        get => (PreviewFit)GetValue(FitModeProperty);
        set => SetValue(FitModeProperty, value);
    }

    public bool HasContent => (bool)GetValue(HasContentProperty);

    /// <summary>Optional metrics sink for measured preview FPS.</summary>
    public PerformanceMetrics? Metrics { get; set; }

    /// <summary>Raised on the UI thread when the viewport or fit mode changed (the renderer should re-render).</summary>
    public event EventHandler? ViewportChanged;

    public (int Width, int Height, double DpiScale) Viewport => (_viewWidth, _viewHeight, Volatile.Read(ref _dpiScale));

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Volatile.Write(ref _dpiScale, scale);
        _viewWidth = (int)Math.Floor(ActualWidth * scale);
        _viewHeight = (int)Math.Floor(ActualHeight * scale);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFitMode()
    {
        if (FitMode == PreviewFit.Fit)
        {
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _image.Stretch = Stretch.Uniform;
            _image.StretchDirection = StretchDirection.DownOnly; // never upscale: keeps glyphs crisp
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        }
        else
        {
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _image.Stretch = Stretch.None;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        }

        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Present(PreviewBuffer buffer)
    {
        Interlocked.Exchange(ref _pending, buffer)?.Release();
        if (Interlocked.Exchange(ref _presentScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, PresentPending);
        }
    }

    public void Clear()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Clear);
            return;
        }

        Interlocked.Exchange(ref _pending, null)?.Release();
        _bitmap = null;
        _image.Source = null;
        SetValue(HasContentPropertyKey, false);
    }

    private void PresentPending()
    {
        Interlocked.Exchange(ref _presentScheduled, 0);
        var buffer = Interlocked.Exchange(ref _pending, null);
        if (buffer is null)
        {
            return;
        }

        try
        {
            double dpi = 96 * buffer.DpiScale;
            if (_bitmap is null || _bitmap.PixelWidth != buffer.Width || _bitmap.PixelHeight != buffer.Height
                || Math.Abs(_bitmap.DpiX - dpi) > 0.01)
            {
                _bitmap = new WriteableBitmap(buffer.Width, buffer.Height, dpi, dpi, PixelFormats.Bgr32, null);
                _image.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, buffer.Width, buffer.Height), buffer.Pixels, buffer.Stride, 0);
            if (!HasContent)
            {
                SetValue(HasContentPropertyKey, true);
            }

            Metrics?.PreviewRate.Tick();
        }
        finally
        {
            buffer.Release();
        }
    }
}
