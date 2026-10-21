using System.Runtime.InteropServices;

namespace NexPane.Shell.Tray;

/// <summary>Win32 declarations for the notification-area icon (shell32 Shell_NotifyIconW, user32 icon loading).</summary>
internal static unsafe partial class NativeMethods
{
    internal const uint NIM_ADD = 0x0;
    internal const uint NIM_MODIFY = 0x1;
    internal const uint NIM_DELETE = 0x2;
    internal const uint NIM_SETVERSION = 0x4;
    internal const uint NIF_MESSAGE = 0x1;
    internal const uint NIF_ICON = 0x2;
    internal const uint NIF_TIP = 0x4;
    internal const uint NIF_SHOWTIP = 0x80;
    internal const uint NOTIFYICON_VERSION_4 = 4;
    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x10;
    internal const int SM_CXSMICON = 49;

    /// <summary>NOTIFYICONDATAW (shellapi.h), full Vista+ layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONDATAW
    {
        internal uint cbSize;
        internal nint hWnd;
        internal uint uID;
        internal uint uFlags;
        internal uint uCallbackMessage;
        internal nint hIcon;
        internal fixed char szTip[128];
        internal uint dwState;
        internal uint dwStateMask;
        internal fixed char szInfo[256];
        internal uint uVersion;
        internal fixed char szInfoTitle[64];
        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal nint hBalloonIcon;

        internal void SetTip(string text)
        {
            var length = Math.Min(text.Length, 127);
            fixed (char* tip = szTip)
            {
                var target = new Span<char>(tip, 128);
                text.AsSpan(0, length).CopyTo(target);
                target[length..].Clear();
            }
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    internal static bool ShellNotifyIcon(uint message, ref NOTIFYICONDATAW data)
    {
        fixed (NOTIFYICONDATAW* pointer = &data)
        {
            return Shell_NotifyIconW(message, pointer);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
