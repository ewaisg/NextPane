using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.Launch;

/// <summary>An absolute PIDL allocated by the shell, freed with <c>ILFree</c> (ARCHITECTURE §11, "Item identity").</summary>
internal sealed partial class SafePidlHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePidlHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        ILFree(handle);
        return true;
    }

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(nint pidl);
}

internal static unsafe partial class ShellLaunchNative
{
    internal const uint SeeMaskInvokeIdList = 0x0000000C;
    internal const uint SeeMaskNoAsync = 0x00000100;
    internal const uint SeeMaskFlagNoUi = 0x00000400;
    internal const uint SeeMaskUnicode = 0x00004000;
    internal const uint SeeMaskFlagLogUsage = 0x04000000;

    internal const int SwHide = 0;
    internal const int SwShowNormal = 1;
    internal const int SwShowMinimized = 2;
    internal const int SwShowMaximized = 3;

    /// <summary>SHELLEXECUTEINFOW (shellapi.h); string members point at pinned managed strings for the duration of the call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Hwnd;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellExecuteEx(ref ShellExecuteInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(string name, nint bindContext, out SafePidlHandle pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetShortPathName(string longPath, char* shortPath, uint bufferLength);
}
