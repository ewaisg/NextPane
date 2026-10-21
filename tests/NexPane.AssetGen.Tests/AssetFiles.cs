using System.Buffers.Binary;

namespace NexPane.AssetGen.Tests;

/// <summary>Locates the repository and parses the binary formats the generator writes, independently of the generator code.</summary>
internal static class AssetFiles
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("NexPane.sln not found above the test output folder.");
    });

    internal static string Root => RootLazy.Value;

    internal static string Assets => Path.Combine(Root, "assets");

    internal static string LogoSvg => Path.Combine(Assets, "brand", "nexpane-logo.svg");

    internal static string MarkSvg => Path.Combine(Assets, "brand", "nexpane-mark.svg");

    internal sealed record IcoEntry(int Width, int Height, int BitCount, byte[] Data)
    {
        internal bool IsPng => Data.Length > 8 && Data.AsSpan(0, 8).SequenceEqual(PngSignature);
    }

    internal static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Parses ICONDIR / ICONDIRENTRY records and slices out each image.</summary>
    internal static IReadOnlyList<IcoEntry> ReadIco(byte[] bytes)
    {
        var span = bytes.AsSpan();
        BinaryPrimitives.ReadUInt16LittleEndian(span[0..]).Should().Be(0, "ICONDIR.idReserved");
        BinaryPrimitives.ReadUInt16LittleEndian(span[2..]).Should().Be(1, "ICONDIR.idType must be 1 (icon)");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var entries = new List<IcoEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = span.Slice(6 + (16 * i), 16);
            var width = e[0] == 0 ? 256 : e[0];
            var height = e[1] == 0 ? 256 : e[1];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            (offset + size).Should().BeLessThanOrEqualTo(bytes.Length, "an ICONDIRENTRY must point inside the file");
            entries.Add(new IcoEntry(width, height, bitCount, bytes.AsSpan(offset, size).ToArray()));
        }

        return entries;
    }

    internal sealed record PngHeader(int Width, int Height, int BitDepth, int ColorType);

    internal static PngHeader ReadPngHeader(byte[] png)
    {
        png.AsSpan(0, 8).SequenceEqual(PngSignature).Should().BeTrue("PNG signature");
        System.Text.Encoding.ASCII.GetString(png, 12, 4).Should().Be("IHDR");
        return new PngHeader(
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)),
            png[24],
            png[25]);
    }

    internal sealed record BmpHeader(int Width, int Height, int BitCount, int Compression, int PixelOffset, int FileSize);

    internal static BmpHeader ReadBmpHeader(byte[] bmp)
    {
        bmp[0].Should().Be((byte)'B');
        bmp[1].Should().Be((byte)'M');
        BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(14)).Should().Be(40, "BITMAPINFOHEADER");
        return new BmpHeader(
            BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)),
            BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)),
            BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28)),
            BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(30)),
            BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10)),
            BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
    }

    /// <summary>WCAG 2.x relative luminance of an sRGB colour.</summary>
    internal static double Luminance(byte r, byte g, byte b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    /// <summary>WCAG contrast ratio between two luminances.</summary>
    internal static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
}
