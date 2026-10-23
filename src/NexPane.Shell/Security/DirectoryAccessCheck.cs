using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.Security;

/// <summary>
/// <c>AccessCheck</c> of a client token for <c>FILE_LIST_DIRECTORY</c> on a directory's security descriptor (§7.5 privacy
/// enforcement, P3.14). The Index Service runs as LocalSystem and must only stream entries the connected user could
/// list in Explorer.
/// </summary>
public sealed class DirectoryAccessCheck : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const int TokenDuplicateAccess = 0x2 | 0x4 | 0x8; // TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY
    private const int SecurityIdentification = 1;
    private const int TokenImpersonation = 2;

    private readonly SafeAccessTokenHandle _impersonationToken;

    /// <summary>Wraps a token (primary, or an impersonation token at identification level or above) of the client. The
    /// duplicate is an identification-level impersonation token: enough for <c>AccessCheck</c>, and the only level
    /// available when the client connected with <c>SECURITY_IDENTIFICATION</c> (§7.5).</summary>
    public DirectoryAccessCheck(SafeAccessTokenHandle clientToken)
    {
        ArgumentNullException.ThrowIfNull(clientToken);
        if (!DuplicateTokenEx(clientToken, TokenDuplicateAccess, IntPtr.Zero, SecurityIdentification, TokenImpersonation, out var duplicate))
        {
            throw new UnauthorizedAccessException($"DuplicateTokenEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        _impersonationToken = duplicate;
    }

    /// <summary>For the current process user (tests, in-process elevated mode).</summary>
    public static DirectoryAccessCheck ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query | TokenAccessLevels.Impersonate);
        return new DirectoryAccessCheck(identity.AccessToken);
    }

    /// <summary>Checks whether the client may list <paramref name="directoryPath"/>. A descriptor that cannot be read
    /// counts as denied (fail closed).</summary>
    public unsafe bool CanList(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        var path = FileSystem.Win32FileApi.ToExtendedPath(directoryPath);
        const uint information = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;
        GetFileSecurity(path, information, null, 0, out var needed);
        if (needed == 0)
        {
            return false;
        }

        var descriptor = new byte[needed];
        fixed (byte* sd = descriptor)
        {
            if (!GetFileSecurity(path, information, sd, needed, out _))
            {
                return false;
            }

            var mapping = new GenericMapping
            {
                GenericRead = 0x00120089,      // FILE_GENERIC_READ
                GenericWrite = 0x00120116,     // FILE_GENERIC_WRITE
                GenericExecute = 0x001200A0,   // FILE_GENERIC_EXECUTE
                GenericAll = 0x001F01FF,       // FILE_ALL_ACCESS
            };
            var privileges = stackalloc byte[256];
            var privilegesLength = 256u;
            if (!AccessCheck(sd, _impersonationToken, FileListDirectory, ref mapping, privileges, ref privilegesLength, out _, out var granted))
            {
                return false;
            }

            return granted;
        }
    }

    public void Dispose() => _impersonationToken.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }

    [DllImport("advapi32.dll", EntryPoint = "GetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileSecurity(string fileName, uint requestedInformation, byte* securityDescriptor, uint length, out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool AccessCheck(byte* securityDescriptor, SafeAccessTokenHandle clientToken, uint desiredAccess, ref GenericMapping mapping,
        byte* privilegeSet, ref uint privilegeSetLength, out uint grantedAccess, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existingToken, int desiredAccess, IntPtr tokenAttributes, int impersonationLevel,
        int tokenType, out SafeAccessTokenHandle newToken);
}
