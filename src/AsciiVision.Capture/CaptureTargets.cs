using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AsciiVision.Capture.Interop;

namespace AsciiVision.Capture;

public sealed record MonitorInfo(IntPtr Handle, int Index, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string DisplayName => $"Display {Index + 1}{(IsPrimary ? " (primary)" : string.Empty)} — {Width}×{Height}";

    public override string ToString() => DisplayName;
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId)
{
    public string DisplayName => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  ·  {ProcessName}";

    public override string ToString() => DisplayName;
}

/// <summary>Enumerates monitors and capturable top-level windows.</summary>
public static class CaptureTargets
{
    /// <summary>Monitors in physical pixels (the process is per-monitor DPI aware), primary first.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var found = new List<(IntPtr Handle, NativeMethods.MONITORINFOEX Info)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(h, ref info))
            {
                found.Add((h, info));
            }

            return true;
        }, IntPtr.Zero);

        return found
            .OrderByDescending(m => (m.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.Info.rcMonitor.Left)
            .ThenBy(m => m.Info.rcMonitor.Top)
            .Select((m, i) => new MonitorInfo(m.Handle, i, m.Info.szDevice, m.Info.rcMonitor.Left, m.Info.rcMonitor.Top,
                m.Info.rcMonitor.Width, m.Info.rcMonitor.Height, (m.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0))
            .ToList();
    }

    /// <summary>
    /// Visible, titled, non-tool, non-cloaked top-level windows (the same filtering the Windows
    /// capture picker applies). Windows of this app — including its console output process — are
    /// excluded, since capturing them would feed the output back into itself.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetWindows()
    {
        int ownPid = Environment.ProcessId;
        string ownName = Process.GetCurrentProcess().ProcessName;
        var windows = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsCapturableWindow(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid)
            {
                return true;
            }

            int length = NativeMethods.GetWindowTextLength(hwnd);
            var sb = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!processNames.TryGetValue(pid, out var processName))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    processName = p.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    processName = string.Empty;
                }

                processNames[pid] = processName;
            }

            if (string.Equals(processName, ownName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            windows.Add(new WindowInfo(hwnd, title, processName, (int)pid));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
        {
            return false;
        }

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((style & NativeMethods.WS_DISABLED) != 0 || (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return NativeMethods.GetWindowTextLength(hwnd) > 0;
    }

    public static bool IsWindowAlive(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    public static bool IsMinimized(IntPtr hwnd) => NativeMethods.IsIconic(hwnd);
}
