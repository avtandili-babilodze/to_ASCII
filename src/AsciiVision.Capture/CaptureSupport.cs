using AsciiVision.Capture.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;

namespace AsciiVision.Capture;

/// <summary>Feature detection for Windows.Graphics.Capture and related window features.</summary>
public static class CaptureSupport
{
    /// <summary>Windows 10 1903 (build 18362): first release where WGC works for HWND/HMONITOR from desktop apps.</summary>
    public static bool IsCaptureSupported
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            {
                return false;
            }

            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static string UnsupportedReason =>
        !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
            ? "Screen and window capture require Windows 10 version 1903 or newer."
            : "Windows.Graphics.Capture is not available on this system (it may be disabled by policy or missing graphics support).";

    /// <summary>Hiding the cursor needs Windows 10 2004 (API contract 10).</summary>
    public static bool CanToggleCursor =>
        SafeIsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled");

    /// <summary>WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004 (build 19041).</summary>
    public static bool CanExcludeFromCapture => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Excludes (or re-includes) one of our windows from all screen capture so a monitor capture
    /// does not show the app's own preview recursively (the "infinite mirror").
    /// </summary>
    public static bool SetExcludedFromCapture(IntPtr hwnd, bool excluded)
    {
        if (!CanExcludeFromCapture || hwnd == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.SetWindowDisplayAffinity(hwnd, excluded ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
    }

    private static bool SafeIsPropertyPresent(string type, string property)
    {
        try
        {
            return ApiInformation.IsPropertyPresent(type, property);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
