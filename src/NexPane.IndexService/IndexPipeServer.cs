using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexPane.Indexing.Service;
using NexPane.Shell.Security;

namespace NexPane.IndexService;

/// <summary>
/// Named-pipe server of the Index Service (§7.5, P3.13): <c>\\.\pipe\NexPane.Index.v1</c> with
/// <c>SYSTEM</c> full control, <c>INTERACTIVE</c> read/write and <c>NETWORK</c> denied. Each connection is served by
/// <see cref="IndexPipeSession"/>; the client's identification-level token is captured (<see cref="PipeClientToken"/>) and every
/// streamed directory is checked with <see cref="DirectoryAccessCheck"/> against it (P3.14).
/// </summary>
internal sealed partial class IndexPipeServer(IIndexVolumeBackend backend, ILogger<IndexPipeServer> logger, string pipeName = IndexProtocol.PipeName) : BackgroundService
{
    private const int MaxInstances = 16;

    internal static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is { } user && !user.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            // Console-hosted (development/tests): the hosting user needs to create further pipe instances.
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clients = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                64 * 1024, 64 * 1024, CreateSecurity());
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            foreach (var faulted in clients.Where(t => t.IsFaulted))
            {
                LogSessionFaulted(logger, faulted.Exception!.GetBaseException());
            }

            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(ServeAsync(pipe, stoppingToken));
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            DirectoryAccessCheck? access = null;
            try
            {
                // Called by the session after the client's Hello was read (impersonation needs received data).
                await IndexPipeSession.RunAsync(pipe, backend, () =>
                {
                    access = PipeClientToken.Capture(pipe.SafePipeHandle, out var user, out var level);
                    LogClientConnected(logger, user ?? "(unknown)", level);
                    return access.CanList;
                }, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                LogClientRejected(logger, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogClientRejected(logger, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogStopping(logger);
            }
            finally
            {
                access?.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Index client connected as {User} (token level {Level}).")]
    private static partial void LogClientConnected(ILogger logger, string user, TokenImpersonationLevel level);

    [LoggerMessage(Level = LogLevel.Error, Message = "Index client session failed.")]
    private static partial void LogSessionFaulted(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index client rejected: {Reason}")]
    private static partial void LogClientRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Index client session ended by service shutdown.")]
    private static partial void LogStopping(ILogger logger);
}
