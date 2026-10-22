using System.Runtime.InteropServices;

namespace NexPane.Shell.FileSystem;

/// <summary>
/// Sends items to the Recycle Bin with <c>SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO)</c> on a dedicated STA thread
/// (shell operations must run on an STA, HANDOFF gotcha 1). The rich IFileOperation path with progress and undo is
/// task P4.20; this is the non-UI provider path.
/// </summary>
internal static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoErrorUi = 0x400;

    public static Task SendAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();
        if (path.Length >= 260)
        {
            throw new PathTooLongException($"'{path}' exceeds MAX_PATH; the Recycle Bin shell API cannot handle it. Delete permanently instead.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var operation = new ShFileOpStruct
                {
                    Func = FoDelete,
                    From = path + "\0\0",
                    Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
                };
                var result = SHFileOperation(ref operation);
                if (result != 0 || operation.AnyOperationsAborted)
                {
                    completion.TrySetException(new IOException($"Moving '{path}' to the Recycle Bin failed (shell error 0x{result:X})."));
                }
                else
                {
                    completion.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "NexPane recycle bin (STA)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);
}
