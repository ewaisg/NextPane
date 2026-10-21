using System.Diagnostics;
using System.Runtime.InteropServices;
using NexPane.Shell.Tray;

namespace NexPane.Shell.Tests;

/// <summary>task P2.07: the notification-area icon wrapper (Shell_NotifyIconW) with the generated tray icons.</summary>
public sealed partial class TrayIconTests
{
    private static string IconsFolder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir!.FullName, "assets", "icons");
        }
    }

    [Theory]
    [InlineData(TrayIconVariant.FullColor, "tray.ico")]
    [InlineData(TrayIconVariant.GlyphForLightTaskbar, "tray-light.ico")]
    [InlineData(TrayIconVariant.GlyphForDarkTaskbar, "tray-dark.ico")]
    public void Every_variant_maps_to_a_generated_icon_file(TrayIconVariant variant, string expected)
    {
        TrayIcon.FileNameFor(variant).Should().Be(expected);
        File.Exists(Path.Combine(IconsFolder, expected)).Should().BeTrue();
    }

    [Fact]
    public void Taskbar_theme_selects_a_single_colour_glyph()
    {
        TrayIcon.VariantForCurrentTaskbar().Should().BeOneOf(TrayIconVariant.GlyphForLightTaskbar, TrayIconVariant.GlyphForDarkTaskbar);
    }

    [Fact]
    public void Native_struct_matches_the_shellapi_x64_layout()
    {
        Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>().Should().Be(976, "sizeof(NOTIFYICONDATAW) on x64 Windows Vista and later");
    }

    [Fact]
    public void Constructor_rejects_a_null_window()
    {
        var act = () => new TrayIcon(0, 1, 0x8001);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Icon_is_added_updated_and_removed_from_the_notification_area()
    {
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            Assert.Skip("No Explorer shell (notification area) is running in this session.");
        }

        var window = CreateWindowExW(0, "STATIC", "NexPane tray test", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, 0, 0);
        window.Should().NotBe(0);
        try
        {
            using var tray = new TrayIcon(window, 1, 0x8001);
            tray.Show(Path.Combine(IconsFolder, TrayIcon.FileNameFor(TrayIconVariant.FullColor)), "NexPane");
            tray.IsVisible.Should().BeTrue();
            tray.Show(Path.Combine(IconsFolder, TrayIcon.FileNameFor(TrayIcon.VariantForCurrentTaskbar())), "NexPane - indexing");
            tray.IsVisible.Should().BeTrue();
            tray.Hide();
            tray.IsVisible.Should().BeFalse();
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public void Show_reports_a_missing_icon_file()
    {
        var window = CreateWindowExW(0, "STATIC", "NexPane tray test", 0x80000000, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            using var tray = new TrayIcon(window, 2, 0x8001);
            var act = () => tray.Show(Path.Combine(IconsFolder, "absent.ico"), "NexPane");
            act.Should().Throw<System.ComponentModel.Win32Exception>();
            tray.IsVisible.Should().BeFalse();
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
