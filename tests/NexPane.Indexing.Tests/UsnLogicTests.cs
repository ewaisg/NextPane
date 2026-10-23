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
    public void Journal_changes_below_a_denied_directory_are_hidden_at_any_depth()
    {
        // Regression (QA P3.36): only the direct parent was checked, so names created deep inside a denied folder leaked.
        var paths = new Dictionary<ulong, string>
        {
            [20] = @"M:\public",
            [21] = @"M:\private",
            [22] = @"M:\private\a",
            [23] = @"M:\private\a\b",
            [24] = @"M:\public\deep\er",
        };
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"M:\private" };
        var filter = new JournalAccessFilter(p => !denied.Contains(p), frn => paths.GetValueOrDefault(frn), 5, @"M:\");

        filter.IsVisible(Record(100, 5, "top.txt", UsnReason.FileCreate)).Should().BeTrue();
        filter.IsVisible(Record(101, 20, "a.txt", UsnReason.FileCreate)).Should().BeTrue();
        filter.IsVisible(Record(102, 24, "deep.txt", UsnReason.FileCreate)).Should().BeTrue();
        filter.IsVisible(Record(103, 5, "private", UsnReason.BasicInfoChange, directory: true)).Should().BeTrue("the denied folder itself is listed in a listable root");
        filter.IsVisible(Record(104, 21, "secret.txt", UsnReason.FileCreate)).Should().BeFalse();
        filter.IsVisible(Record(105, 22, "nested.txt", UsnReason.FileCreate)).Should().BeFalse();
        filter.IsVisible(Record(106, 23, "deepest-secret.txt", UsnReason.FileCreate)).Should().BeFalse("every ancestor must be listable");
        filter.IsVisible(Record(107, 99, "orphan.txt", UsnReason.FileCreate)).Should().BeFalse("unresolvable parents fail closed");
    }

    [Fact]
    public void Journal_access_cache_is_invalidated_by_security_changes_and_directory_moves_and_is_bounded()
    {
        var paths = new Dictionary<ulong, string> { [20] = @"M:\shared", [21] = @"M:\vault", [22] = @"M:\shared\project" };
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filter = new JournalAccessFilter(p => !denied.Contains(p), frn => paths.GetValueOrDefault(frn), 5, @"M:\", capacity: 16);

        filter.IsVisible(Record(100, 20, "a.txt", UsnReason.FileCreate)).Should().BeTrue();

        // ACL tightened on M:\shared: the directory's SECURITY_CHANGE record invalidates the cached verdicts.
        denied.Add(@"M:\shared");
        filter.IsVisible(Record(20, 5, "shared", UsnReason.SecurityChange | UsnReason.Close, directory: true)).Should().BeTrue();
        filter.IsVisible(Record(101, 20, "b.txt", UsnReason.FileCreate)).Should().BeFalse();
        filter.IsVisible(Record(102, 22, "c.txt", UsnReason.FileCreate)).Should().BeFalse();

        // ACL relaxed again, then "project" is moved into the denied vault: the rename invalidates and its files hide.
        denied.Clear();
        denied.Add(@"M:\vault");
        filter.IsVisible(Record(20, 5, "shared", UsnReason.SecurityChange, directory: true)).Should().BeTrue();
        filter.IsVisible(Record(103, 22, "d.txt", UsnReason.FileCreate)).Should().BeTrue();
        paths[22] = @"M:\vault\project";
        filter.IsVisible(Record(22, 21, "project", UsnReason.RenameNewName, directory: true)).Should().BeFalse();
        filter.IsVisible(Record(104, 22, "e.txt", UsnReason.FileCreate)).Should().BeFalse("the moved directory is now below a denied folder");
        filter.Invalidations.Should().BeGreaterThanOrEqualTo(3);

        // Bounded: thousands of distinct directories never grow the cache past its capacity.
        for (ulong d = 1000; d < 5000; d++)
        {
            paths[d] = $@"M:\shared\d{d}";
            filter.IsVisible(Record(d + 100_000, d, "x.txt", UsnReason.FileCreate)).Should().BeTrue();
            filter.CachedVerdicts.Should().BeLessThanOrEqualTo(16 + 3);
        }
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

    [Fact]
    public void Ntfs_metafiles_and_the_extend_subtree_are_excluded_by_identity_not_by_name()
    {
        static ulong Ref(ulong record, ulong sequence = 1) => record | (sequence << 48);
        var root = Ref(5, 5);
        var entries = new Dictionary<ulong, MftStat>
        {
            [root] = Dir(root, root, "", 1),
            [Ref(0)] = File(Ref(0), root, "$MFT"),
            [Ref(2)] = File(Ref(2), root, "$LogFile"),
            [Ref(6)] = File(Ref(6), root, "$Bitmap"),
            [Ref(11)] = Dir(Ref(11), root, "$Extend", 1),
            [Ref(24)] = File(Ref(24), Ref(11), "$Quota"),
            [Ref(25)] = File(Ref(25), Ref(11), "$ObjId"),
            [Ref(27)] = Dir(Ref(27), Ref(11), "$RmMetadata", 1),
            [Ref(28)] = Dir(Ref(28), Ref(27), "$Txf", 1),
            [Ref(29)] = File(Ref(29), Ref(28), "log.blf"),
            [Ref(40)] = File(Ref(40), root, "$user-file.txt"),
            [Ref(41)] = Dir(Ref(41), root, "$projects", 1),
            [Ref(42)] = File(Ref(42), Ref(41), "notes.txt"),
            [Ref(43)] = File(Ref(43), root, "readme.md"),
        };

        var kept = NtfsMetafiles.Exclude(entries);

        kept.Values.Select(e => e.Name).Should().BeEquivalentTo("", "$user-file.txt", "$projects", "notes.txt", "readme.md");
        var excludedDirectories = NtfsMetafiles.ExcludedDirectories(entries, kept);
        excludedDirectories.Should().BeEquivalentTo([Ref(11), Ref(27), Ref(28)]);

        // Journal changes: metafiles and anything below $Extend are dropped; user "$" names are kept.
        NtfsMetafiles.IsExcludedChange(Ref(0), root, excludedDirectories).Should().BeTrue();
        NtfsMetafiles.IsExcludedChange(Ref(77), Ref(11), excludedDirectories).Should().BeTrue();
        NtfsMetafiles.IsExcludedChange(Ref(78), Ref(28), excludedDirectories).Should().BeTrue();
        NtfsMetafiles.IsExcludedChange(Ref(79), root, excludedDirectories).Should().BeFalse();
        NtfsMetafiles.IsExcludedChange(Ref(80), Ref(41), excludedDirectories).Should().BeFalse();
    }

    [Fact]
    public void Journal_fixture_records_pass_the_metafile_filter()
    {
        var path = Path.Combine(Path.GetDirectoryName(ContentFixtures.Directory)!, "usn", "journal-v3.bin");
        var parsed = UsnRecordParser.ParseBuffer(System.IO.File.ReadAllBytes(path));

        parsed.Records.Should().NotBeEmpty();
        parsed.Records.Should().AllSatisfy(r => NtfsMetafiles.IsExcludedChange(r.FileReferenceLow, r.ParentReferenceLow, new HashSet<ulong>()).Should().BeFalse());
        NtfsMetafiles.IsReserved(0x0005000000000005).Should().BeFalse("the root directory is not a metafile");
        NtfsMetafiles.IsReserved(0x000100000000000B).Should().BeTrue("record 11 is $Extend");
    }

    private static UsnRecord Record(ulong frn, ulong parent, string name, UsnReason reason, bool directory = false) =>
        new(2, 0, frn, parent, 0, 0, reason, 0, 0, directory ? 0x10u : 0x20u, name, [], 0);

    private static MftStat Dir(ulong frn, ulong parent, string name, uint securityId) => new(frn, parent, name, 0x10, 0, 0, 0, true, securityId);

    private static MftStat File(ulong frn, ulong parent, string name) => new(frn, parent, name, 0x20, 1, 0, 0, false, 9);
}
