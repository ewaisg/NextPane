using System.ComponentModel;

namespace NexPane.Shell.Launch;

/// <summary>Maps <c>ShellExecuteEx</c> failures to <see cref="ShellLaunchStatus"/>.</summary>
internal static class ShellLaunchErrors
{
    // Win32 error codes (winerror.h).
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotEnoughMemory = 8;
    internal const int ErrorOutOfMemory = 14;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorInvalidName = 123;
    internal const int ErrorBadExeFormat = 193;
    internal const int ErrorFilenameExceedsRange = 206;
    internal const int ErrorElevationRequired = 740;
    internal const int ErrorNoAssociation = 1155;
    internal const int ErrorDdeFail = 1156;
    internal const int ErrorDllNotFound = 1157;
    internal const int ErrorCancelled = 1223;

    // Legacy SE_ERR_* values returned in SHELLEXECUTEINFO.hInstApp (shellapi.h).
    internal const int SeErrFileNotFound = 2;
    internal const int SeErrPathNotFound = 3;
    internal const int SeErrAccessDenied = 5;
    internal const int SeErrOutOfMemory = 8;
    internal const int SeErrDllNotFound = 32;
    internal const int SeErrShare = 26;
    internal const int SeErrAssocIncomplete = 27;
    internal const int SeErrDdeTimeout = 28;
    internal const int SeErrDdeFail = 29;
    internal const int SeErrDdeBusy = 30;
    internal const int SeErrNoAssoc = 31;

    private const int FacilityWin32 = 7;

    /// <summary>Maps a <c>GetLastError</c> value from a failed <c>ShellExecuteExW</c>.</summary>
    public static ShellLaunchStatus FromWin32Error(int error) => error switch
    {
        0 => ShellLaunchStatus.Success,
        ErrorFileNotFound => ShellLaunchStatus.FileNotFound,
        ErrorPathNotFound => ShellLaunchStatus.PathNotFound,
        ErrorAccessDenied => ShellLaunchStatus.AccessDenied,
        ErrorNotEnoughMemory or ErrorOutOfMemory => ShellLaunchStatus.OutOfMemory,
        ErrorSharingViolation => ShellLaunchStatus.SharingViolation,
        ErrorInvalidName => ShellLaunchStatus.InvalidName,
        ErrorBadExeFormat => ShellLaunchStatus.BadExecutableFormat,
        ErrorFilenameExceedsRange => ShellLaunchStatus.PathTooLong,
        ErrorElevationRequired => ShellLaunchStatus.ElevationRequired,
        ErrorNoAssociation => ShellLaunchStatus.NoAssociation,
        ErrorDdeFail => ShellLaunchStatus.DdeFailure,
        ErrorDllNotFound => ShellLaunchStatus.DllNotFound,
        ErrorCancelled => ShellLaunchStatus.CancelledByUser,
        _ => ShellLaunchStatus.Failed,
    };

    /// <summary>Maps the legacy <c>hInstApp</c> code, used when <c>GetLastError</c> returned 0 after a failure.</summary>
    public static (ShellLaunchStatus Status, int Win32Error) FromSeErr(int seErr) => seErr switch
    {
        SeErrFileNotFound => (ShellLaunchStatus.FileNotFound, ErrorFileNotFound),
        SeErrPathNotFound => (ShellLaunchStatus.PathNotFound, ErrorPathNotFound),
        SeErrAccessDenied => (ShellLaunchStatus.AccessDenied, ErrorAccessDenied),
        SeErrOutOfMemory => (ShellLaunchStatus.OutOfMemory, ErrorNotEnoughMemory),
        SeErrShare => (ShellLaunchStatus.SharingViolation, ErrorSharingViolation),
        SeErrAssocIncomplete => (ShellLaunchStatus.IncompleteAssociation, ErrorNoAssociation),
        SeErrDdeTimeout or SeErrDdeFail or SeErrDdeBusy => (ShellLaunchStatus.DdeFailure, ErrorDdeFail),
        SeErrNoAssoc => (ShellLaunchStatus.NoAssociation, ErrorNoAssociation),
        SeErrDllNotFound => (ShellLaunchStatus.DllNotFound, ErrorDllNotFound),
        _ => (ShellLaunchStatus.Failed, seErr),
    };

    /// <summary>Maps an HRESULT (e.g. from <c>SHParseDisplayName</c>); <c>HRESULT_FROM_WIN32</c> values use the Win32 table.</summary>
    public static ShellLaunchResult FromHResult(int hr)
    {
        if (hr >= 0)
        {
            return ShellLaunchResult.Success;
        }

        if (((hr >> 16) & 0x1FFF) == FacilityWin32)
        {
            return FromWin32(hr & 0xFFFF);
        }

        return new ShellLaunchResult(ShellLaunchStatus.Failed, hr, new Win32Exception(hr).Message);
    }

    /// <summary>Builds the result for a Win32 error code, including the system message.</summary>
    public static ShellLaunchResult FromWin32(int error) =>
        error == 0 ? ShellLaunchResult.Success : new ShellLaunchResult(FromWin32Error(error), error, new Win32Exception(error).Message);
}
