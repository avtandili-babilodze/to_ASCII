using System.Windows.Threading;

namespace AsciiVision.Rendering.Glyphs;

/// <summary>
/// A dedicated STA thread with its own WPF dispatcher for font measurement and glyph
/// rasterization. WPF text objects require an STA thread, and doing this work here keeps it
/// off the UI thread and usable from worker threads (preview, export, tests).
/// </summary>
public sealed class WpfGlyphThread : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public WpfGlyphThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ASCII Vision glyph rasterizer",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public T Invoke<T>(Func<T> work)
    {
        if (_dispatcher.CheckAccess())
        {
            return work();
        }

        return _dispatcher.Invoke(work);
    }

    public void Dispose()
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.InvokeShutdown();
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
