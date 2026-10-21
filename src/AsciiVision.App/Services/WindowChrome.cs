using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AsciiVision.App.Services;

/// <summary>Small Win32 helpers for window appearance.</summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar to match the theme (no effect on unsupported builds).</summary>
    public static void UseDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
        }
    }

    public static IntPtr GetHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();
}
