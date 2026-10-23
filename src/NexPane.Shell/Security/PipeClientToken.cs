using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.Security;

/// <summary>
/// Captures the token of a named-pipe client for <see cref="DirectoryAccessCheck"/> (§7.5, P3.13):
/// <c>ImpersonateNamedPipeClient</c> → <c>OpenThreadToken</c> → <c>RevertToSelf</c> in one synchronous call, and fails
/// fast if the thread could not be reverted (it must never run service code as the client). Clients connect with
/// <c>SECURITY_IDENTIFICATION</c>, so the token can be inspected and access-checked but not used to act as the user.
/// The thread's impersonation state is verified after the revert, because the service's thread-pool threads later
/// open files (<c>GetFileSecurity</c>), which fails with ERROR_BAD_IMPERSONATION_LEVEL on a thread left impersonating.
/// </summary>
public static class PipeClientToken
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    /// <summary>Access check for the connected client; <paramref name="userName"/> is DOMAIN\user when resolvable.</summary>
    /// <exception cref="UnauthorizedAccessException">The client cannot be identified (no data read yet, or anonymous).</exception>
    public static DirectoryAccessCheck Capture(SafePipeHandle serverPipe, out string? userName, out TokenImpersonationLevel level)
    {
        ArgumentNullException.ThrowIfNull(serverPipe);
        if (!ImpersonateNamedPipeClient(serverPipe))
        {
            throw new UnauthorizedAccessException($"ImpersonateNamedPipeClient failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        SafeAccessTokenHandle token;
        int openError;
        try
        {
            // openAsSelf: the thread token is opened with the service's own access, not the (identification) client's.
            var opened = OpenThreadToken(GetCurrentThread(), TokenQuery | TokenDuplicate, true, out token);
            openError = opened ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (!RevertToSelf())
            {
                Environment.FailFast($"RevertToSelf failed with Win32 error {Marshal.GetLastPInvokeError()} after capturing a pipe client token.");
            }
        }

        using (token)
        {
            if (openError != 0 || token.IsInvalid)
            {
                throw new UnauthorizedAccessException($"OpenThreadToken failed with Win32 error {openError}.");
            }

            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            level = identity.ImpersonationLevel;
            try
            {
                userName = identity.Name;
            }
            catch (SystemException ex) when (ex is IdentityNotMappedException or UnauthorizedAccessException)
            {
                userName = identity.User?.Value;
            }

            return new DirectoryAccessCheck(token);
        }
    }

    /// <summary>True when the calling thread is impersonating (diagnostics and tests).</summary>
    public static bool ThreadIsImpersonating()
    {
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return current.ImpersonationLevel != TokenImpersonationLevel.None;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}
