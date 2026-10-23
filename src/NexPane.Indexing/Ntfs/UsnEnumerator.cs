using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Ntfs;

/// <summary>
/// Whole-volume <c>FSCTL_ENUM_USN_DATA</c> enumeration (§7.3 "names + parent FRNs + attributes", §17 budget ≤ 5 s per
/// 1M MFT records): walks every in-use MFT record through the change journal's enumeration IOCTL in 1 MB batches and
/// yields one V2/V3 record per file or directory. Requires a volume handle opened with administrator rights.
/// </summary>
public static class UsnEnumerator
{
    private const int BufferSize = 1 << 20;

    public static IEnumerable<UsnRecord> Enumerate(NtfsVolume volume, long highUsn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var buffer = new byte[BufferSize];
        ulong next = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = volume.EnumUsnData(next, highUsn, buffer);
            if (result.Error == NtfsVolume.ErrorHandleEof)
            {
                yield break;
            }

            if (!result.Succeeded)
            {
                throw new IOException($"FSCTL_ENUM_USN_DATA on {volume.Root} failed with Win32 error {result.Error}.", result.Error);
            }

            var parsed = UsnRecordParser.ParseBuffer(buffer.AsSpan(0, result.BytesReturned));
            if (parsed.Error is not null)
            {
                throw new InvalidDataException($"FSCTL_ENUM_USN_DATA on {volume.Root} returned a malformed buffer: {parsed.Error}");
            }

            foreach (var record in parsed.Records)
            {
                yield return record;
            }

            if (parsed.Records.Count == 0 || (ulong)parsed.Next == next)
            {
                yield break;
            }

            next = (ulong)parsed.Next;
        }
    }
}
