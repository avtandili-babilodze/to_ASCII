using System.Runtime.InteropServices;

namespace AsciiVision.Rendering.Console;

internal static class ConsoleNative
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 1;
    internal const uint FILE_SHARE_WRITE = 2;
    internal const uint OPEN_EXISTING = 3;

    internal const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    internal const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
    internal const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    internal const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    internal const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    internal const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    internal const uint CTRL_C_EVENT = 0;
    internal const uint CTRL_BREAK_EVENT = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SMALL_RECT
    {
        public short Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CONSOLE_CURSOR_INFO
    {
        public uint dwSize;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bVisible;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    internal struct CHAR_INFO
    {
        [FieldOffset(0)]
        public char UnicodeChar;

        [FieldOffset(2)]
        public ushort Attributes;
    }

    internal delegate bool HandlerRoutine(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll")]
    internal static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetConsoleTitleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleTitle(string title);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleScreenBufferSize(IntPtr handle, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCursorInfo(IntPtr handle, ref CONSOLE_CURSOR_INFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleCursorInfo(IntPtr handle, out CONSOLE_CURSOR_INFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WriteConsoleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool WriteConsole(IntPtr handle, char* buffer, uint count, out uint written, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WriteConsoleOutputW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool WriteConsoleOutput(IntPtr handle, CHAR_INFO* buffer, COORD size, COORD coord, ref SMALL_RECT region);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
