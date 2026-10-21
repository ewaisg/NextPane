using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NexPane.Shell.Tray;

/// <summary>Which of the generated tray icon files to show (ARCHITECTURE section 19.1, task P2.05/P2.07).</summary>
public enum TrayIconVariant
{
    /// <summary><c>tray.ico</c>: the full-colour app mark.</summary>
    FullColor,

    /// <summary><c>tray-light.ico</c>: dark single-colour glyph for a light taskbar.</summary>
    GlyphForLightTaskbar,

    /// <summary><c>tray-dark.ico</c>: white single-colour glyph for a dark taskbar.</summary>
    GlyphForDarkTaskbar,
}

/// <summary>
/// A notification-area (system tray) icon owned by a window, implemented with <c>Shell_NotifyIconW</c>
/// (NOTIFYICON_VERSION_4). The owner window receives <see cref="CallbackMessage"/> for mouse and keyboard events.
/// The window is passed as an HWND (<see cref="nint"/>) so this layer stays free of WPF.
/// Create, update and dispose it on the owner window's UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly nint _owner;
    private readonly uint _id;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    /// <param name="ownerWindow">HWND that owns the icon and receives <paramref name="callbackMessage"/>.</param>
    /// <param name="id">Identifier of the icon, unique per owner window.</param>
    /// <param name="callbackMessage">Window message (WM_APP range recommended) sent to the owner for icon events.</param>
    public TrayIcon(nint ownerWindow, uint id, uint callbackMessage)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("A valid owner window handle is required.", nameof(ownerWindow));
        }

        _owner = ownerWindow;
        _id = id;
        CallbackMessage = callbackMessage;
    }

    /// <summary>The window message sent to the owner for icon events.</summary>
    public uint CallbackMessage { get; }

    /// <summary>True while the icon is shown in the notification area.</summary>
    public bool IsVisible => _added;

    /// <summary>File name of the generated .ico for a variant (copied next to NexPane.exe under <c>Assets\</c>).</summary>
    public static string FileNameFor(TrayIconVariant variant) => variant switch
    {
        TrayIconVariant.FullColor => "tray.ico",
        TrayIconVariant.GlyphForLightTaskbar => "tray-light.ico",
        TrayIconVariant.GlyphForDarkTaskbar => "tray-dark.ico",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown tray icon variant."),
    };

    /// <summary>
    /// The single-colour variant matching the current taskbar theme
    /// (<c>HKCU\...\Themes\Personalize\SystemUsesLightTheme</c>; a missing value means dark, the Windows default).
    /// </summary>
    public static TrayIconVariant VariantForCurrentTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0
            ? TrayIconVariant.GlyphForLightTaskbar
            : TrayIconVariant.GlyphForDarkTaskbar;
    }

    /// <summary>Adds the icon to the notification area, or replaces its image and tooltip if it is already shown.</summary>
    /// <param name="iconPath">Path of a multi-size .ico; the small-icon size for the owner window's DPI is loaded.</param>
    /// <param name="tooltip">Tooltip text (truncated to 127 characters).</param>
    public void Show(string iconPath, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(iconPath);
        ArgumentNullException.ThrowIfNull(tooltip);

        var dpi = NativeMethods.GetDpiForWindow(_owner);
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi == 0 ? 96u : dpi);
        var icon = NativeMethods.LoadImage(0, iconPath, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
        if (icon == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not load tray icon '{iconPath}'.");
        }

        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        data.hIcon = icon;
        data.SetTip(tooltip);

        if (!_added)
        {
            if (!NativeMethods.ShellNotifyIcon(NativeMethods.NIM_ADD, ref data))
            {
                NativeMethods.DestroyIcon(icon);
                throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed; the notification area is not available.");
            }

            _added = true;
            var version = CreateData(0);
            version.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.ShellNotifyIcon(NativeMethods.NIM_SETVERSION, ref version);
        }
        else if (!NativeMethods.ShellNotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            NativeMethods.DestroyIcon(icon);
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_MODIFY) failed.");
        }

        if (_icon != 0)
        {
            NativeMethods.DestroyIcon(_icon);
        }

        _icon = icon;
    }

    /// <summary>Removes the icon from the notification area. Showing it again is allowed.</summary>
    public void Hide()
    {
        if (_added)
        {
            var data = CreateData(0);
            NativeMethods.ShellNotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != 0)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    /// <summary>Removes the icon and releases its icon handle.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Hide();
            _disposed = true;
        }
    }

    private NativeMethods.NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _owner,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
    };
}
