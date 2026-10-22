using System.Security.AccessControl;
using System.Security.Principal;
using NexPane.Core.Indexing;
using NexPane.Indexing.Ntfs;
using NexPane.Shell.FileSystem;
using NexPane.Shell.Security;

namespace NexPane.Indexing.Tests;

public sealed class UsnLogicTests
{
    private static readonly UsnJournalInfo Journal = new(JournalId: 0xAA, FirstUsn: 1000, NextUsn: 5000, LowestValidUsn: 1000, MaxUsn: long.MaxValue, MaximumSize: 32, AllocationDelta: 8);

    [Theory]
    [InlineData(null, null, JournalStartMode.FullScan, "No persisted")]
    [InlineData(0xAAL, 4000L, JournalStartMode.Resume, "Resuming")]
    [InlineData(0xAAL, 1000L, JournalStartMode.Resume, "Resuming")]
    [InlineData(0xBBL, 4000L, JournalStartMode.FullScan, "Journal id changed")]
    [InlineData(0xAAL, 999L, JournalStartMode.FullScan, "older than the first retained USN")]
    [InlineData(0xAAL, 9000L, JournalStartMode.FullScan, "ahead of the journal")]
    public void Journal_resume_decisions(long? journalId, long? lastUsn, JournalStartMode mode, string reason)
    {
        var decision = JournalResume.Decide(journalId, lastUsn, Journal);

        decision.Mode.Should().Be(mode);
        decision.Reason.Should().Contain(reason);
        decision.StartUsn.Should().Be(mode == JournalStartMode.Resume ? lastUsn!.Value : Journal.NextUsn);
    }

    [Fact]
    public void Journal_records_translate_to_coalesced_deltas()
    {
        var records = new[]
        {
            Record(10, 5, "a.txt", UsnReason.FileCreate),
            Record(10, 5, "a.txt", UsnReason.DataExtend),
            Record(10, 5, "a.txt", UsnReason.DataExtend | UsnReason.Close),
            Record(11, 5, "old.txt", UsnReason.RenameOldName),
            Record(11, 7, "new.txt", UsnReason.RenameNewName),
            Record(11, 7, "new.txt", UsnReason.RenameNewName | UsnReason.Close),
            Record(12, 5, "gone.txt", UsnReason.FileDelete | UsnReason.Close),
            Record(13, 5, "open.log", UsnReason.DataExtend),
        };
        var stats = 0;

        var deltas = UsnChangeTranslator.Translate(records, (ulong frn, out uint attr, out long size, out long created, out long modified) =>
        {
            stats++;
            attr = 0x20;
            size = (long)frn * 100;
            created = 1;
            modified = 2;
            return true;
        });

        deltas.Should().HaveCount(6);
        deltas[0].Should().Be(IndexDelta.UpsertById(10, 5, "a.txt", 0x20, 1000, 2, 1));
        deltas[1].Kind.Should().Be(IndexDeltaKind.Move);
        deltas[1].ParentFileId.Should().Be(7UL);
        deltas[2].Kind.Should().Be(IndexDeltaKind.Move);
        deltas[3].Should().Be(IndexDelta.UpsertById(11, 7, "new.txt", 0x20, 1100, 2, 1));
        deltas[4].Should().Be(IndexDelta.RemoveById(12));
        deltas[5].FileId.Should().Be(13UL);
        stats.Should().Be(3, "one stat per closed (or still open) file");
    }

    [Fact]
    public void Access_filter_drops_entries_below_unlistable_directories_and_caches_by_security_id()
    {
        const ulong root = 5;
        var entries = new Dictionary<ulong, MftStat>
        {
            [root] = Dir(root, root, "", 1),
            [20] = Dir(20, root, "public", 1),
            [21] = Dir(21, root, "private", 2),
            [22] = Dir(22, 21, "deeper", 1),
            [30] = File(30, 20, "a.txt"),
            [31] = File(31, 21, "secret.txt"),
            [32] = File(32, 22, "nested.txt"),
            [33] = File(33, root, "top.txt"),
        };
        var filter = new AccessFilter(path => !path.EndsWith(@"\private", StringComparison.Ordinal));

        var visible = filter.Filter(entries, root, @"C:\", frn => entries[frn].SecurityId);

        visible.Select(e => e.Name).Should().BeEquivalentTo("public", "private", "a.txt", "top.txt");
        filter.Checks.Should().Be(2, "security id 1 is checked once (root), id 2 once (private)");
    }

    [Fact]
    public void Access_check_honors_a_deny_ace_for_the_current_user()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexpane-acl-" + Guid.NewGuid().ToString("N"));
        var denied = Directory.CreateDirectory(Path.Combine(root, "denied"));
        var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed"));
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = denied.GetAccessControl();
        security.AddAccessRule(rule);
        denied.SetAccessControl(security);
        try
        {
            using var check = DirectoryAccessCheck.ForCurrentUser();

            check.CanList(denied.FullName).Should().BeFalse();
            check.CanList(allowed.FullName).Should().BeTrue();
            check.CanList(Path.Combine(root, "missing")).Should().BeFalse("unreadable descriptors fail closed");
        }
        finally
        {
            security.RemoveAccessRule(rule);
            denied.SetAccessControl(security);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Elevated")]
    public void Elevated_volume_open_reads_journal_and_mft_of_the_system_drive()
    {
        using var volume = NtfsVolume.Open(Path.GetPathRoot(Environment.SystemDirectory)!);
        volume.TryQueryJournal(out var journal, out var error).Should().BeTrue($"error {error}");
        journal.NextUsn.Should().BeGreaterThan(0);
        var data = volume.GetVolumeData();
        data.BytesPerFileRecordSegment.Should().BeOneOf(1024, 4096);
        var mft = MftReader.ReadAll(volume, TestContext.Current.CancellationToken);
        mft.Values.Should().Contain(s => s.Name == "Windows" && s.IsDirectory);
    }

    private static UsnRecord Record(ulong frn, ulong parent, string name, UsnReason reason) =>
        new(2, 0, frn, parent, 0, 0, reason, 0, 0, 0x20, name, [], 0);

    private static MftStat Dir(ulong frn, ulong parent, string name, uint securityId) => new(frn, parent, name, 0x10, 0, 0, 0, true, securityId);

    private static MftStat File(ulong frn, ulong parent, string name) => new(frn, parent, name, 0x20, 1, 0, 0, false, 9);
}
