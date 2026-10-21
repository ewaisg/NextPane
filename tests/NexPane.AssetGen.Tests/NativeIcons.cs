using System.Runtime.InteropServices;

namespace NexPane.AssetGen.Tests;

/// <summary>Windows icon loader used to check that every .ico layer loads the way Explorer and the shell load it.</summary>
internal static partial class NativeIcons
{
    internal const uint ImageIcon = 1;
    internal const uint LoadFromFile = 0x00000010;

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
}
