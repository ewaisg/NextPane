using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace NexPane.Shell.Content;

/// <summary>
/// Extracts text through the Windows IFilter registered for a file type (<c>LoadIFilter</c> from query.dll), e.g.
/// <c>.rtf</c>, <c>.doc</c>, <c>.odt</c> (§7.6). All COM calls run on one dedicated STA thread with a message loop
/// substitute (a blocking work queue), and every filter is created and released on that thread (HANDOFF gotcha 1).
/// </summary>
public sealed class IFilterTextReader : IDisposable
{
    private const int FilterEEndOfChunks = unchecked((int)0x80041700);
    private const int FilterENoMoreText = unchecked((int)0x80041701);
    private const int FilterENoText = unchecked((int)0x80041705);
    private const int FilterSLastText = 0x00041709;
    private const int ChunkText = 1;
    private const uint InitFlags = 1 | 2 | 4 | 8 | 16 | 64; // canon paragraphs/line breaks/hyphens/spaces, index attributes, indexing only

    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;

    public IFilterTextReader()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                action();
            }
        })
        {
            IsBackground = true,
            Name = "NexPane IFilter (STA)",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Returns the text, or null when no IFilter is registered for the type or it yields no text.</summary>
    public Task<string?> ReadAsync(string path, int maxChars, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                completion.TrySetResult(Read(path, maxChars, ct));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (ex is OperationCanceledException)
                {
                    completion.TrySetCanceled(ct);
                }
                else
                {
                    completion.TrySetException(ex);
                }
            }
        }, ct);
        return completion.Task;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _work.Dispose();
    }

    private static string? Read(string path, int maxChars, CancellationToken ct)
    {
        var hr = LoadIFilter(path, IntPtr.Zero, out var filter);
        if (hr != 0 || filter is null)
        {
            return null; // no filter registered for this type
        }

        try
        {
            hr = filter.Init(InitFlags, 0, IntPtr.Zero, out _);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var text = new StringBuilder();
            var buffer = new char[4096];
            while (text.Length < maxChars)
            {
                ct.ThrowIfCancellationRequested();
                hr = filter.GetChunk(out var chunk);
                if (hr == FilterEEndOfChunks)
                {
                    break;
                }

                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                if ((chunk.Flags & ChunkText) == 0)
                {
                    continue;
                }

                if (chunk.BreakType != 0 && text.Length > 0)
                {
                    text.Append('\n');
                }

                while (text.Length < maxChars)
                {
                    var count = (uint)buffer.Length;
                    hr = filter.GetText(ref count, buffer);
                    if (hr == FilterENoMoreText || hr == FilterENoText)
                    {
                        break;
                    }

                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }

                    text.Append(buffer, 0, (int)Math.Min(count, (uint)buffer.Length));
                    if (hr == FilterSLastText)
                    {
                        break;
                    }
                }
            }

            if (text.Length > maxChars)
            {
                text.Length = maxChars;
            }

            var result = text.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatChunk
    {
        public uint IdChunk;
        public int BreakType;
        public int Flags;
        public uint Locale;
        public Guid PropertySet;
        public uint PropertyKind;
        public IntPtr PropertyIdOrName;
        public uint IdChunkSource;
        public uint StartSource;
        public uint LengthSource;
    }

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig]
        int Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pFlags);

        [PreserveSig]
        int GetChunk(out StatChunk pStat);

        [PreserveSig]
        int GetText(ref uint pcwcBuffer, [Out, MarshalAs(UnmanagedType.LPArray)] char[] awcBuffer);

        [PreserveSig]
        int GetValue(out IntPtr ppPropValue);

        [PreserveSig]
        int BindRegion(IntPtr origPos, ref Guid riid, out IntPtr ppunk);
    }

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, [MarshalAs(UnmanagedType.Interface)] out IFilter? ppIUnk);
}
