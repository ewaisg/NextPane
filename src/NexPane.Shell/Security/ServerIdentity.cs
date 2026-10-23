using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.Security;

/// <summary>What the Service Control Manager reports about a service (§7.5 server verification, P3.13).</summary>
/// <param name="ProcessId">Process id while running, otherwise 0.</param>
/// <param name="Running">SERVICE_RUNNING.</param>
/// <param name="Account">Configured account (<c>LocalSystem</c> for the Index Service).</param>
/// <param name="BinaryPath">Configured command line (image path and arguments).</param>
public sealed record ServiceProcessInfo(int ProcessId, bool Running, string? Account, string? BinaryPath)
{
    /// <summary>True when the service runs as LocalSystem (the SCM reports <c>LocalSystem</c>; null also means LocalSystem).</summary>
    public bool RunsAsLocalSystem => Account is null || Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                                     Account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>The executable file name of <see cref="BinaryPath"/> (quotes and arguments removed).</summary>
    public string? ImageFileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BinaryPath))
            {
                return null;
            }

            var path = BinaryPath.Trim();
            if (path.StartsWith('"'))
            {
                var close = path.IndexOf('"', 1);
                path = close > 0 ? path[1..close] : path.Trim('"');
            }
            else
            {
                var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe > 0 ? path[..(exe + 4)] : path;
            }

            return Path.GetFileName(path);
        }
    }
}

/// <summary>
/// Identifies the process behind a named pipe and queries the Service Control Manager, so a pipe client can verify that
/// it talks to the installed service and not to a process that created the pipe name first (pipe squatting, §7.5).
/// Works unelevated: <c>GetNamedPipeServerProcessId</c> and <c>SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG</c>.
/// </summary>
public static class ServerIdentity
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;

    /// <summary>The process id of the server end of a connected client pipe.</summary>
    public static bool TryGetPipeServerProcessId(SafePipeHandle clientPipe, out int processId)
    {
        ArgumentNullException.ThrowIfNull(clientPipe);
        var ok = GetNamedPipeServerProcessId(clientPipe, out var pid);
        processId = ok ? (int)pid : 0;
        return ok;
    }

    /// <summary>Status and configuration of a service, or null when it is not installed (or not readable).</summary>
    public static unsafe ServiceProcessInfo? TryQueryService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                ServiceStatusProcess status;
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, &status, (uint)sizeof(ServiceStatusProcess), out _))
                {
                    return null;
                }

                QueryServiceConfig(service, null, 0, out var needed);
                if (needed == 0)
                {
                    return null;
                }

                var buffer = new byte[needed];
                fixed (byte* config = buffer)
                {
                    if (!QueryServiceConfig(service, config, needed, out _))
                    {
                        return null;
                    }

                    var c = (QueryServiceConfigW*)config;
                    var running = status.CurrentState == ServiceRunning;
                    return new ServiceProcessInfo(running ? (int)status.ProcessId : 0, running,
                        Marshal.PtrToStringUni(c->ServiceStartName), Marshal.PtrToStringUni(c->BinaryPathName));
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigW
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryServiceStatusEx(IntPtr service, int infoLevel, void* buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryServiceConfig(IntPtr service, void* config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
