using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using NexPane.Core.Indexing;
using NexPane.Indexing.Ntfs;
using NexPane.Indexing.Service;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;
using NexPane.Shell.Security;

namespace NexPane.IndexService.Tests;

public sealed class IndexProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<IndexMessage> Messages => new()
    {
        new HelloMessage(IndexProtocol.Version),
        new ListVolumesMessage(),
        new VolumeInfoMessage(@"\\?\Volume{12345678-1234-1234-1234-123456789012}\", @"C:\", 0xDEADBEEF, "NTFS", 0x1D2F3E4A5B6C7D8E, 123456789, 0x0005000000000005),
        new EnumerateMessage(@"\\?\Volume{x}\"),
        new EnumerateDoneMessage(42),
        new SubscribeMessage(@"\\?\Volume{x}\", 99),
        new ErrorMessage(-5, "boom — ünïcødé"),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_round_trips(IndexMessage message)
    {
        var frame = IndexProtocol.Encode(message, FrameOptions.Final);

        var (decoded, flags) = IndexProtocol.Decode(frame.AsSpan(4));

        decoded.Should().BeEquivalentTo(message, o => o.RespectingRuntimeTypes());
        flags.Should().Be(FrameOptions.Final);
        BitConverter.ToUInt32(frame, 0).Should().Be((uint)(frame.Length - 4));
    }

    [Fact]
    public void Batches_round_trip()
    {
        var entries = new EntryBatchMessage([new WireEntry(1, 5, 0x20, 1234, 5, 6, "a.txt"), new WireEntry(2, 5, 0x10, 0, 7, 8, "名前")]);
        var changes = new ChangeBatchMessage([new WireChange(0x100, 3, 5, 0x20, "new.txt", 777), new WireChange(0x200, 4, 5, 0, "", 778)]);

        ((EntryBatchMessage)IndexProtocol.Decode(IndexProtocol.Encode(entries).AsSpan(4)).Message).Entries.Should().Equal(entries.Entries);
        ((ChangeBatchMessage)IndexProtocol.Decode(IndexProtocol.Encode(changes).AsSpan(4)).Message).Changes.Should().Equal(changes.Changes);
    }

    [Fact]
    public async Task Stream_reader_rejects_oversized_and_truncated_frames()
    {
        var oversized = new MemoryStream(BitConverter.GetBytes((uint)(IndexProtocol.MaxFrameLength + 1)));
        var act = () => IndexProtocol.ReadAsync(oversized, Ct);
        await act.Should().ThrowAsync<ProtocolException>().WithMessage("*Invalid frame length*");

        var frame = IndexProtocol.Encode(new HelloMessage(1));
        var truncated = new MemoryStream(frame[..^2]);
        act = () => IndexProtocol.ReadAsync(truncated, Ct);
        await act.Should().ThrowAsync<ProtocolException>().WithMessage("*Truncated*");

        (await IndexProtocol.ReadAsync(new MemoryStream(), Ct)).Should().BeNull("a clean end of stream is not an error");
    }

    [Fact]
    public void Fuzzing_one_hundred_thousand_frames_never_crashes_or_allocates_unboundedly()
    {
        var random = new Random(1234);
        var valid = Messages.Select(m => IndexProtocol.Encode(m.Data)).Concat(
        [
            IndexProtocol.Encode(new EntryBatchMessage([new WireEntry(1, 5, 0x20, 1, 2, 3, "x.txt")])),
            IndexProtocol.Encode(new ChangeBatchMessage([new WireChange(1, 2, 3, 4, "y", 5)])),
        ]).ToArray();
        var maxAllocated = 0L;
        var rejected = 0;
        for (var i = 0; i < 100_000; i++)
        {
            byte[] body;
            if (i % 2 == 0)
            {
                body = new byte[random.Next(0, 256)];
                random.NextBytes(body);
                if (body.Length >= 2 && random.Next(3) == 0)
                {
                    BitConverter.TryWriteBytes(body.AsSpan(0, 2), (ushort)random.Next(1, 10)); // plausible type
                }
            }
            else
            {
                body = valid[random.Next(valid.Length)][4..];
                for (var k = random.Next(1, 6); k > 0; k--)
                {
                    body[random.Next(body.Length)] = (byte)random.Next(256);
                }
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                IndexProtocol.Decode(body);
            }
            catch (ProtocolException)
            {
                rejected++;
            }

            maxAllocated = Math.Max(maxAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        rejected.Should().BeGreaterThan(50_000);
        maxAllocated.Should().BeLessThan(1024 * 1024, "decoding a frame of at most 256 bytes must never allocate more than a small bounded amount");
    }

    [Fact]
    public async Task Server_streams_a_volume_to_the_client_and_filters_folders_the_client_cannot_list()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexpane-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "public", "sub"));
        File.WriteAllText(Path.Combine(root, "public", "sub", "visible.txt"), "v");
        var secret = Directory.CreateDirectory(Path.Combine(root, "secret"));
        File.WriteAllText(Path.Combine(secret.FullName, "hidden-from-client.txt"), "s");
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = secret.GetAccessControl();
        security.AddAccessRule(deny);
        secret.SetAccessControl(security);
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = new IndexPipeServer(new TreeBackend(root), NullLogger<IndexPipeServer>.Instance, pipeName);
        await server.StartAsync(Ct);
        try
        {
            var client = new IndexServiceClient(pipeName, TimeSpan.FromSeconds(5), verifyServer: InProcess);
            (await client.ProbeAsync(Ct)).Should().Be(IndexServiceState.Running);

            await using var connection = await client.ConnectAsync(Ct);
            var volumes = await connection.ListVolumesAsync(Ct);
            volumes.Should().ContainSingle().Which.Root.Should().Be(root + "\\");

            await using var store = new IndexStore();
            var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(root + "\\", VolumeKind.NtfsFixed), TreeBackend.RootReference, Ct);
            var next = await connection.EnumerateIntoAsync(volumes[0].VolumeGuid, store, segment, Ct);

            next.Should().Be(777);
            var snapshot = store.CurrentGeneration;
            var names = Enumerable.Range(1, snapshot.Count(segment.Id) - 1).Select(i => snapshot.GetSegment(segment.Id).GetName(i).ToString()).ToList();
            names.Should().BeEquivalentTo("public", "sub", "visible.txt", "secret");
            names.Should().NotContain("hidden-from-client.txt", "the client may not list 'secret'");
            var visible = Enumerable.Range(1, snapshot.Count(segment.Id) - 1).Single(i => snapshot.GetSegment(segment.Id).GetName(i).SequenceEqual("visible.txt"));
            snapshot.GetSegment(segment.Id).GetFullPath(visible).Should().Be(Path.Combine(root, "public", "sub", "visible.txt"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            security.RemoveAccessRule(deny);
            secret.SetAccessControl(security);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Protocol_version_mismatch_is_reported_as_outdated()
    {
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        var server = new IndexPipeServer(new TreeBackend(Path.GetTempPath()), NullLogger<IndexPipeServer>.Instance, pipeName);
        await server.StartAsync(Ct);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, Ct);
            await IndexProtocol.WriteAsync(pipe, new HelloMessage(IndexProtocol.Version + 1), Ct);
            var reply = await IndexProtocol.ReadAsync(pipe, Ct);

            reply!.Value.Message.Should().BeOfType<ErrorMessage>().Which.Code.Should().Be(IndexPipeSession.ErrorVersionMismatch);
            (await new IndexServiceClient("NexPane.Index.absent." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100)).ProbeAsync(Ct))
                .Should().BeOneOf(IndexServiceState.NotInstalled, IndexServiceState.Stopped);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Pipe_security_grants_interactive_users_and_denies_network()
    {
        var rules = IndexPipeServer.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        rules.Should().Contain(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value && r.AccessControlType == AccessControlType.Allow);
        rules.Should().Contain(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.NetworkSid, null).Value && r.AccessControlType == AccessControlType.Deny);
        rules.Should().Contain(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value && (r.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl);
    }

    [Fact]
    public async Task A_pipe_squatter_is_rejected_by_the_default_server_verification()
    {
        // This test process creates the pipe first, as a local attacker could: it is not the NexPaneIndex service.
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        var squatter = new IndexPipeServer(new TreeBackend(Path.GetTempPath()), NullLogger<IndexPipeServer>.Instance, pipeName);
        await squatter.StartAsync(Ct);
        try
        {
            var client = new IndexServiceClient(pipeName, TimeSpan.FromSeconds(5), serviceName: "NexPaneIndexTestAbsent" + Guid.NewGuid().ToString("N"));
            var connect = () => client.ConnectAsync(Ct);
            await connect.Should().ThrowAsync<UntrustedIndexServerException>().WithMessage($"*process {Environment.ProcessId}*");
            (await client.ProbeAsync(Ct)).Should().Be(IndexServiceState.NotInstalled, "a squatted pipe does not count as a running service");
            IndexServiceClient.IsInstalledService(IndexServiceClient.ServiceName, Environment.ProcessId).Should().BeFalse();
        }
        finally
        {
            await squatter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_client_connects_at_identification_level_so_the_server_cannot_act_as_the_user()
    {
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(Ct);
            (await IndexProtocol.ReadAsync(server, Ct))!.Value.Message.Should().BeOfType<HelloMessage>();
            using var access = PipeClientToken.Capture(server.SafePipeHandle, out var user, out var level);
            var reverted = !PipeClientToken.ThreadIsImpersonating();
            var canList = access.CanList(Path.GetTempPath());
            await IndexProtocol.WriteAsync(server, new HelloMessage(IndexProtocol.Version), Ct);
            return (level, user, reverted, canList);
        }, Ct);

        await using var connection = await new IndexServiceClient(pipeName, TimeSpan.FromSeconds(5), verifyServer: InProcess).ConnectAsync(Ct);

        var (level, user, reverted, canList) = await serving;
        level.Should().Be(TokenImpersonationLevel.Identification);
        user.Should().Be(WindowsIdentity.GetCurrent().Name);
        reverted.Should().BeTrue("the service thread must not keep running as the client");
        canList.Should().BeTrue("an identification-level token is enough for AccessCheck");
    }

    [Fact]
    public async Task A_journal_reset_travels_as_an_error_code_and_triggers_a_full_rescan_in_service_mode()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexpane-svc-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "dir"));
        File.WriteAllText(Path.Combine(root, "dir", "file.txt"), "x");
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        var backend = new ResettingBackend(new TreeBackend(root));
        var server = new IndexPipeServer(backend, NullLogger<IndexPipeServer>.Instance, pipeName);
        await server.StartAsync(Ct);
        try
        {
            var client = new IndexServiceClient(pipeName, TimeSpan.FromSeconds(5), verifyServer: InProcess);
            var volume = backend.ListVolumes()[0];
            await using var store = new IndexStore();
            var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(volume.Root, VolumeKind.NtfsFixed), volume.RootFileReference, Ct);
            var source = new UsnVolumeSource(store, segment, volume);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            // The persisted position is valid, so the source resumes; the service then reports the journal reset.
            var running = source.RunAsync(client, new JournalPosition(unchecked((long)volume.JournalId), volume.NextUsn), null, cts.Token);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (source.FullScans == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }

            source.FullScans.Should().Be(1);
            source.Decision!.Mode.Should().Be(JournalStartMode.FullScan);
            source.Decision.Reason.Should().Be(ResettingBackend.Reason);
            backend.Subscriptions.Should().BeGreaterThanOrEqualTo(1);
            var snapshot = store.CurrentGeneration;
            Enumerable.Range(1, snapshot.Count(segment.Id) - 1).Select(i => snapshot.GetSegment(segment.Id).GetName(i).ToString())
                .Should().Contain("file.txt", "the rescan re-enumerated the volume");

            var raw = new MemoryStream();
            await IndexProtocol.WriteAsync(raw, new ErrorMessage(IndexPipeSession.ErrorJournalReset, "any text"), Ct);
            raw.Position = 0;
            (await IndexProtocol.ReadAsync(raw, Ct))!.Value.Message.Should().Be(new ErrorMessage(5, "any text"), "the code, not the text, carries the meaning");

            await cts.CancelAsync();
            await running.ContinueWith(_ => { }, CancellationToken.None);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool InProcess(int serverProcessId) => serverProcessId == Environment.ProcessId;

    [Fact]
    [Trait("Category", "Elevated")]
    public async Task Elevated_ntfs_backend_streams_the_system_volume_to_an_unprivileged_style_client()
    {
        var pipeName = "NexPane.Index.test." + Guid.NewGuid().ToString("N");
        var server = new IndexPipeServer(new NtfsVolumeBackend(), NullLogger<IndexPipeServer>.Instance, pipeName);
        await server.StartAsync(Ct);
        try
        {
            await using var connection = await new IndexServiceClient(pipeName, TimeSpan.FromSeconds(5), verifyServer: InProcess).ConnectAsync(Ct);
            var volume = (await connection.ListVolumesAsync(Ct)).First(v => v.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
            await using var store = new IndexStore();
            var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(volume.Root, VolumeKind.NtfsFixed, VolumeSerial: volume.Serial), volume.RootFileReference, Ct);
            await connection.EnumerateIntoAsync(volume.VolumeGuid, store, segment, Ct);
            store.CurrentGeneration.Count(segment.Id).Should().BeGreaterThan(10_000);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Reports a journal reset on the first subscription (the message text is deliberately unrelated).</summary>
    private sealed class ResettingBackend(IIndexVolumeBackend inner) : IIndexVolumeBackend
    {
        public const string Reason = "Journal 7 was recreated.";
        private int _subscriptions;

        public int Subscriptions => Volatile.Read(ref _subscriptions);

        public IReadOnlyList<VolumeInfoMessage> ListVolumes() => inner.ListVolumes();

        public Task<long> EnumerateAsync(string volumeGuid, Func<string, bool> canList, Func<IReadOnlyList<WireEntry>, Task> sink, CancellationToken ct) =>
            inner.EnumerateAsync(volumeGuid, canList, sink, ct);

        public Task SubscribeAsync(string volumeGuid, long fromUsn, Func<string, bool> canList, Func<IReadOnlyList<WireChange>, Task> sink, CancellationToken ct) =>
            Interlocked.Increment(ref _subscriptions) == 1
                ? throw new JournalResetException(Reason)
                : inner.SubscribeAsync(volumeGuid, fromUsn, canList, sink, ct);
    }

    /// <summary>Test backend: serves a real temp tree, applying the client's canList check to each directory.</summary>
    private sealed class TreeBackend(string root) : IIndexVolumeBackend
    {
        public const ulong RootReference = 5;
        private const string Guid = @"\\?\Volume{00000000-0000-0000-0000-00000000test}\";

        public IReadOnlyList<VolumeInfoMessage> ListVolumes() => [new VolumeInfoMessage(Guid, root.TrimEnd('\\') + "\\", 1, "NTFS", 7, 777, RootReference)];

        public async Task<long> EnumerateAsync(string volumeGuid, Func<string, bool> canList, Func<IReadOnlyList<WireEntry>, Task> sink, CancellationToken ct)
        {
            var batch = new List<WireEntry>();
            var next = 100UL;
            void Walk(string dir, ulong parent)
            {
                if (!canList(dir))
                {
                    return;
                }

                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var id = next++;
                    batch.Add(new WireEntry(id, parent, (uint)info.Attributes, info is FileInfo f ? f.Length : 0, 1, 1, info.Name));
                    if (info is DirectoryInfo)
                    {
                        Walk(info.FullName, id);
                    }
                }
            }

            Walk(root, RootReference);
            await sink(batch);
            return 777;
        }

        public Task SubscribeAsync(string volumeGuid, long fromUsn, Func<string, bool> canList, Func<IReadOnlyList<WireChange>, Task> sink, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);
    }
}
