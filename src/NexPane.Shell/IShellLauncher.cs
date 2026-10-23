namespace NexPane.Shell;

/// <summary>
/// Opens items the way Explorer does on double-click: <c>ShellExecuteExW</c> with <c>SEE_MASK_INVOKEIDLIST |
/// SEE_MASK_NOASYNC | SEE_MASK_FLAG_LOG_USAGE</c> and the item's PIDL, executed on the Shell STA pool (ARCHITECTURE
/// §5, §10.8, §11). The returned task completes on a thread-pool thread, so awaiting it never blocks the UI thread.
/// </summary>
public interface IShellLauncher
{
    /// <summary>Invokes <paramref name="verb"/> (null = the item's default verb) on a parsing name, with error UI allowed.</summary>
    Task<ShellLaunchResult> OpenAsync(string parsingName, string? verb, nint ownerHwnd);

    /// <summary>Invokes a launch request. Cancellation applies only while the request is still queued.</summary>
    Task<ShellLaunchResult> LaunchAsync(ShellLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>One <c>ShellExecuteEx</c> invocation.</summary>
/// <param name="ParsingName">A file-system path (long paths and <c>\\?\</c> prefixes accepted) or any shell parsing name such as <c>::{CLSID}</c>.</param>
/// <param name="Verb">Canonical verb (<c>open</c>, <c>edit</c>, <c>print</c>, <c>runas</c>, or a registered custom verb); null = default verb.</param>
public sealed record ShellLaunchRequest(string ParsingName, string? Verb = null)
{
    /// <summary>Owner window for any UI the shell or the handler shows; 0 = none.</summary>
    public nint OwnerHwnd { get; init; }

    /// <summary>Working directory; null = the item's parent folder for file-system items, otherwise the shell's default.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Command-line parameters passed to an executable target; null for documents.</summary>
    public string? Arguments { get; init; }

    public ShellLaunchWindow Window { get; init; } = ShellLaunchWindow.Normal;

    /// <summary>
    /// Adds <c>SEE_MASK_FLAG_NO_UI</c>: the shell shows no error message box; the failure is returned as a typed result
    /// instead. This does not suppress the "Pick an app" (OpenWith.exe) prompt for a type with no association: that is
    /// the <c>HKCR\Unknown</c> class's own default/<c>open</c> verb, which the shell treats as a successful launch.
    /// Double-click in the file list keeps this false.
    /// </summary>
    public bool SuppressErrorUi { get; init; }
}

/// <summary>Maps to the <c>nShow</c> value passed to the launched application.</summary>
public enum ShellLaunchWindow
{
    Normal,
    Minimized,
    Maximized,
    Hidden,
}

/// <summary>Typed outcome of a launch, mapped from <c>GetLastError</c>, an HRESULT or the legacy <c>SE_ERR_*</c> code.</summary>
public enum ShellLaunchStatus
{
    Success,
    FileNotFound,
    PathNotFound,
    PathTooLong,
    InvalidName,
    AccessDenied,
    SharingViolation,
    NoAssociation,
    IncompleteAssociation,
    DdeFailure,
    DllNotFound,
    BadExecutableFormat,
    ElevationRequired,
    OutOfMemory,
    CancelledByUser,
    Failed,
}

/// <param name="Status">Typed outcome.</param>
/// <param name="NativeError">The Win32 error code (or HRESULT for <see cref="ShellLaunchStatus.Failed"/> from parsing); 0 on success.</param>
/// <param name="Message">System message for <paramref name="NativeError"/>, or null on success.</param>
public readonly record struct ShellLaunchResult(ShellLaunchStatus Status, int NativeError, string? Message)
{
    public static ShellLaunchResult Success { get; } = new(ShellLaunchStatus.Success, 0, null);

    public bool Succeeded => Status == ShellLaunchStatus.Success;
}
