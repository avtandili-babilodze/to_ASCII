using Windows.Graphics.Capture;

namespace AsciiVision.Capture;

/// <summary>
/// The Windows system picker (the same UI other apps use to share a window or screen).
/// Desktop apps must associate the picker with their window handle before showing it.
/// </summary>
public static class CapturePicker
{
    /// <summary>Shows the picker; returns null if the user cancels. Call from the UI thread.</summary>
    public static async Task<GraphicsCaptureItem?> PickAsync(IntPtr ownerWindow)
    {
        if (!CaptureSupport.IsCaptureSupported)
        {
            throw new CaptureException(CaptureSupport.UnsupportedReason);
        }

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        return await picker.PickSingleItemAsync();
    }
}
