using System.Diagnostics;
using System.Runtime.InteropServices;
using AsciiVision.Capture.Interop;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace AsciiVision.Capture;

public sealed record GraphicsCaptureOptions
{
    public bool CaptureCursor { get; init; } = true;

    /// <summary>Frames wider than this are downscaled on the CPU while copying out of the mapped texture.</summary>
    public int MaxFrameWidth { get; init; } = 1920;
}

/// <summary>
/// Monitor or window capture through Windows.Graphics.Capture.
///
/// Frames arrive on a free-threaded frame pool (no UI-thread dependency). Each frame's GPU
/// texture is copied to a CPU-readable staging texture, mapped, and copied (or area-downscaled
/// directly from the mapped memory) into a pooled <see cref="VideoFrame"/>. The capture surface
/// is released before the frame is handed on, so no consumer can touch a recycled GPU surface.
/// </summary>
public sealed class GraphicsCaptureSource : IFrameSource
{
    private const int FramePoolBuffers = 2;

    private readonly Func<GraphicsCaptureItem> _itemFactory;
    private readonly GraphicsCaptureOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private GraphicsCaptureItem? _item;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private IFrameSink? _sink;
    private bool _running;
    private bool _ended;
    private bool _recovering;
    private int _deviceRecreations;
    private long _frameIndex;
    private long _startTimestamp;

    public GraphicsCaptureSource(Func<GraphicsCaptureItem> itemFactory, string displayName, SourceKind kind,
        GraphicsCaptureOptions? options = null, ILogger? logger = null)
    {
        _itemFactory = itemFactory;
        DisplayName = displayName;
        Kind = kind;
        _options = options ?? new GraphicsCaptureOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public static GraphicsCaptureSource ForMonitor(MonitorInfo monitor, GraphicsCaptureOptions? options = null, ILogger? logger = null) =>
        new(() => CaptureInterop.CreateItemForMonitor(monitor.Handle), monitor.DisplayName, SourceKind.Screen, options, logger);

    public static GraphicsCaptureSource ForWindow(WindowInfo window, GraphicsCaptureOptions? options = null, ILogger? logger = null) =>
        new(() =>
        {
            if (!CaptureTargets.IsWindowAlive(window.Handle))
            {
                throw new CaptureException($"The window \"{window.Title}\" no longer exists. Refresh the window list and pick it again.");
            }

            return CaptureInterop.CreateItemForWindow(window.Handle);
        }, window.Title, SourceKind.Window, options, logger);

    /// <summary>Wraps an item returned by the system GraphicsCapturePicker.</summary>
    public static GraphicsCaptureSource ForItem(GraphicsCaptureItem item, SourceKind kind, GraphicsCaptureOptions? options = null, ILogger? logger = null) =>
        new(() => item, item.DisplayName, kind, options, logger);

    public string DisplayName { get; private set; }

    public SourceKind Kind { get; }

    public bool IsLive => true;

    public event EventHandler<SourceEndedEventArgs>? Ended;

    public Task StartAsync(IFrameSink sink, CancellationToken cancellationToken)
    {
        if (!CaptureSupport.IsCaptureSupported)
        {
            throw new CaptureException(CaptureSupport.UnsupportedReason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            _sink = sink;
            try
            {
                _item = _itemFactory();
            }
            catch (CaptureException)
            {
                throw;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
            {
                throw new CaptureException(Kind == SourceKind.Window
                    ? "This window cannot be captured. It may be closed, protected, or owned by an elevated (administrator) process."
                    : "This display cannot be captured.", ex);
            }

            if (!string.IsNullOrWhiteSpace(_item.DisplayName))
            {
                DisplayName = _item.DisplayName;
            }

            _item.Closed += OnItemClosed;
            try
            {
                CreateDeviceAndSession();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DetachSession()();
                _item.Closed -= OnItemClosed;
                _logger.LogError(ex, "Capture initialization failed");
                throw new CaptureException(ex is UnauthorizedAccessException
                    ? "Capture access was denied. Check Windows Settings → Privacy & security → Screen capture."
                    : "Could not start capture: the graphics device could not be initialized (see the log for details).", ex);
            }

            _startTimestamp = Stopwatch.GetTimestamp();
            _running = true;
            _session!.StartCapture();
            _logger.LogInformation("Graphics capture started for {Name} at {W}x{H}", DisplayName, _poolSize.Width, _poolSize.Height);
        }

        return Task.CompletedTask;
    }

    private void CreateDeviceAndSession()
    {
        var (device, context, software) = CaptureInterop.CreateD3DDevice();
        _device = device;
        _context = context;
        if (software)
        {
            _logger.LogWarning("Using the WARP software rasterizer for capture; hardware D3D11 was unavailable");
        }

        _winrtDevice = CaptureInterop.CreateWinRtDevice(device);
        _poolSize = _item!.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        if (CaptureSupport.CanToggleCursor)
        {
            _session.IsCursorCaptureEnabled = _options.CaptureCursor;
        }
    }

    /// <summary>
    /// Detaches all capture objects (call under <see cref="_gate"/>) and returns an action that disposes
    /// them. The action runs outside the lock: a FrameArrived callback blocked on the lock must be able
    /// to proceed (it sees <c>_running == false</c> and returns) while the session shuts down.
    /// </summary>
    private Action DetachSession()
    {
        var pool = _framePool;
        var session = _session;
        var staging = _staging;
        var winrtDevice = _winrtDevice;
        var context = _context;
        var device = _device;
        if (pool is not null)
        {
            pool.FrameArrived -= OnFrameArrived;
        }

        _framePool = null;
        _session = null;
        _staging = null;
        _winrtDevice = null;
        _context = null;
        _device = null;

        return () =>
        {
            session?.Dispose();
            pool?.Dispose();
            staging?.Dispose();
            winrtDevice?.Dispose();
            context?.ClearState();
            context?.Dispose();
            device?.Dispose();
        };
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        RaiseEnded(new SourceEndedEventArgs(SourceEndReason.SourceClosed,
            Kind == SourceKind.Window ? "The captured window was closed." : "The captured display is no longer available."));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        VideoFrame? output = null;
        try
        {
            lock (_gate)
            {
                if (!_running || _framePool is null)
                {
                    return;
                }

                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var contentSize = frame.ContentSize;
                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    return;
                }

                output = CopyFrame(frame, contentSize);

                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // Window resized or display mode changed: future frames need buffers of the new size.
                    _poolSize = contentSize;
                    _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, _poolSize);
                }
            }
        }
        catch (Exception ex) when (IsDeviceLost(ex))
        {
            output?.Dispose();
            output = null;
            HandleDeviceLost(ex);
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or ObjectDisposedException)
        {
            output?.Dispose();
            output = null;
            _logger.LogWarning(ex, "Dropped a capture frame");
        }

        if (output is not null)
        {
            _sink?.OnFrame(output);
        }
    }

    private unsafe VideoFrame CopyFrame(Direct3D11CaptureFrame frame, SizeInt32 contentSize)
    {
        using var texture = CaptureInterop.GetTexture(frame.Surface);
        var desc = texture.Description;
        int width = (int)Math.Min((uint)contentSize.Width, desc.Width);
        int height = (int)Math.Min((uint)contentSize.Height, desc.Height);

        if (_staging is null || _staging.Description.Width != (uint)width || _staging.Description.Height != (uint)height)
        {
            _staging?.Dispose();
            var stagingDesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
            _staging = _device!.CreateTexture2D(stagingDesc);
        }

        _context!.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var timestamp = Stopwatch.GetElapsedTime(_startTimestamp);
            long index = _frameIndex++;
            if (width <= _options.MaxFrameWidth)
            {
                var output = VideoFrame.Rent(width, height, timestamp, index);
                int rowBytes = width * VideoFrame.BytesPerPixel;
                byte* src = (byte*)mapped.DataPointer;
                fixed (byte* dst = output.Buffer)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + (long)y * mapped.RowPitch, dst + (long)y * output.Stride, rowBytes, rowBytes);
                    }
                }

                return output;
            }
            else
            {
                // Large displays (e.g. 4K): area-downscale straight out of mapped memory instead of
                // copying full resolution first. Conversion never needs more than ~2k pixels across.
                int outW = _options.MaxFrameWidth;
                int outH = Math.Max(2, (int)Math.Round(height * (double)outW / width));
                var output = VideoFrame.Rent(outW, outH, timestamp, index);
                fixed (byte* dst = output.Buffer)
                {
                    using var srcMat = Mat.FromPixelData(height, width, MatType.CV_8UC4, mapped.DataPointer, mapped.RowPitch);
                    using var dstMat = Mat.FromPixelData(outH, outW, MatType.CV_8UC4, (IntPtr)dst, output.Stride);
                    Cv2.Resize(srcMat, dstMat, new OpenCvSharp.Size(outW, outH), 0, 0, InterpolationFlags.Area);
                }

                return output;
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private static bool IsDeviceLost(Exception ex)
    {
        const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        int hr = ex switch
        {
            SharpGen.Runtime.SharpGenException s => s.ResultCode.Code,
            COMException c => c.HResult,
            _ => 0,
        };
        return hr is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET;
    }

    private void HandleDeviceLost(Exception ex)
    {
        _logger.LogWarning(ex, "Graphics device lost during capture");
        lock (_gate)
        {
            if (!_running || _recovering)
            {
                return;
            }

            _recovering = true;
        }

        // Recreate on a worker thread: the session must not be torn down from inside its own callback.
        _ = Task.Run(() =>
        {
            bool recovered = false;
            Action dispose;
            lock (_gate)
            {
                dispose = DetachSession();
            }

            dispose();
            lock (_gate)
            {
                if (_running && _deviceRecreations < 3)
                {
                    _deviceRecreations++;
                    try
                    {
                        CreateDeviceAndSession();
                        _session!.StartCapture();
                        recovered = true;
                    }
                    catch (Exception recreateError)
                    {
                        _logger.LogError(recreateError, "Could not recreate the capture device");
                        DetachSession()();
                    }
                }

                _recovering = false;
            }

            if (!recovered)
            {
                RaiseEnded(new SourceEndedEventArgs(SourceEndReason.Failed,
                    "The graphics device was lost (driver reset or GPU change) and capture could not recover.", ex));
            }
        });
    }

    private void RaiseEnded(SourceEndedEventArgs e)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
        }

        Ended?.Invoke(this, e);
    }

    public Task StopAsync()
    {
        Action dispose;
        lock (_gate)
        {
            if (!_running && _item is null)
            {
                return Task.CompletedTask;
            }

            _running = false;
            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }

            dispose = DetachSession();
            _item = null;
            _sink = null;
        }

        dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
