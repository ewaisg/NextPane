using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FlaUI.UIA3;

namespace NexPane.UITests;

/// <summary>task P2.07: the exe carries the multi-size app icon and the main window shows it.</summary>
public sealed partial class AppIconTests
{
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static string AppExe => Path.Combine(AppContext.BaseDirectory, "NexPane.exe");

    [Fact]
    public void ExtractIconEx_finds_an_icon_in_the_built_exe()
    {
        ExtractIconExW(AppExe, -1, 0, 0, 0).Should().BeGreaterThanOrEqualTo(1);

        var large = new nint[1];
        var small = new nint[1];
        ExtractIconExW(AppExe, 0, large, small, 1).Should().BeGreaterThanOrEqualTo(1);
        try
        {
            large[0].Should().NotBe(0);
            small[0].Should().NotBe(0);
        }
        finally
        {
            DestroyIcon(large[0]);
            DestroyIcon(small[0]);
        }
    }

    [Fact]
    public void Exe_icon_group_contains_every_generated_layer()
    {
        GroupIconSizes(AppExe).Should().BeEquivalentTo(RequiredSizes);
    }

    [Fact]
    public void Main_window_icon_is_set()
    {
        using var automation = new UIA3Automation();
        using var app = FlaUI.Core.Application.Launch(AppExe);
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(60));
            window.Should().NotBeNull("NexPane.exe must open its main window");
            window!.Title.Should().Be("NexPane");
            var hwnd = window.Properties.NativeWindowHandle.Value;
            hwnd.Should().NotBe(0);
            SendMessageW(hwnd, WmGetIcon, IconSmall, 0).Should().NotBe(0, "the title bar shows the small app icon");
            SendMessageW(hwnd, WmGetIcon, IconBig, 0).Should().NotBe(0, "Alt+Tab and the taskbar use the big app icon");
        }
        finally
        {
            app.Close();
            if (!app.HasExited)
            {
                app.Kill();
            }
        }
    }

    /// <summary>Reads the first RT_GROUP_ICON resource (GRPICONDIR) of a PE file and returns its layer sizes.</summary>
    internal static IReadOnlyList<int> GroupIconSizes(string path)
    {
        var module = LoadLibraryExW(path, 0, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        module.Should().NotBe(0);
        try
        {
            nint groupName;
            unsafe
            {
                FirstResourceName = 0;
                // Returns FALSE when the callback stops the enumeration early, so the captured name is what is checked.
                _ = EnumResourceNamesW(module, RtGroupIcon, &CaptureFirstName, 0);
                groupName = FirstResourceName;
            }

            groupName.Should().NotBe(0, "the exe must contain an RT_GROUP_ICON resource");
            ((ulong)groupName).Should().BeLessThan(0x10000, "the SDK names the icon group with an integer id (MAKEINTRESOURCE)");

            var resource = FindResourceW(module, groupName, RtGroupIcon);
            resource.Should().NotBe(0);
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);

            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
            return Enumerable.Range(0, count).Select(i => bytes[6 + (14 * i)] == 0 ? 256 : bytes[6 + (14 * i)]).ToList();
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private const uint WmGetIcon = 0x007F;
    private const nint IconSmall = 0;
    private const nint IconBig = 1;
    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const nint RtGroupIcon = 14;

    [ThreadStatic]
    private static nint FirstResourceName;

    [UnmanagedCallersOnly]
    private static int CaptureFirstName(nint module, nint type, nint name, nint param)
    {
        FirstResourceName = name;
        return 0; // stop after the first name
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint[]? large, nint[]? small, uint icons);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint large, nint small, uint icons);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string file, nint reserved, uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int EnumResourceNamesW(nint module, nint type, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint param);

    [LibraryImport("kernel32.dll")]
    private static partial nint FindResourceW(nint module, nint name, nint type);

    [LibraryImport("kernel32.dll")]
    private static partial uint SizeofResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    private static partial nint LoadResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    private static partial nint LockResource(nint data);
}
