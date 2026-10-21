using System.Buffers.Binary;

namespace NexPane.AssetGen;

/// <summary>One image stored in an .ico file.</summary>
internal sealed record IconLayer(RgbaImage Image, bool StoreAsPng);

/// <summary>
/// Writes Windows .ico files (ICONDIR + ICONDIRENTRY + images). Layers are stored either as PNG
/// (Vista+, used for 256 px) or as a 32-bpp BI_RGB DIB with an AND mask (readable by every loader).
/// </summary>
internal static class IcoWriter
{
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    internal static byte[] Write(IReadOnlyList<IconLayer> layers)
    {
        ArgumentOutOfRangeException.ThrowIfZero(layers.Count, nameof(layers));
        var payloads = layers.Select(l =>
        {
            if (l.Image.Width != l.Image.Height || l.Image.Width is < 1 or > 256)
            {
                throw new ArgumentException($"Icon layers must be square and 1-256 px (got {l.Image.Width}x{l.Image.Height}).", nameof(layers));
            }

            return l.StoreAsPng ? l.Image.EncodePng() : EncodeDib(l.Image);
        }).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type: icon
        writer.Write((ushort)layers.Count); // image count

        var offset = IconDirSize + (IconDirEntrySize * layers.Count);
        for (var i = 0; i < layers.Count; i++)
        {
            var size = layers[i].Image.Width;
            writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 means 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);                        // palette colours
            writer.Write((byte)0);                        // reserved
            writer.Write((ushort)1);                      // colour planes
            writer.Write((ushort)32);                     // bits per pixel
            writer.Write((uint)payloads[i].Length);
            writer.Write((uint)offset);
            offset += payloads[i].Length;
        }

        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>BITMAPINFOHEADER (height doubled for XOR + AND), bottom-up 32-bpp BGRA, then a 1-bpp AND mask (1 = transparent).</summary>
    private static byte[] EncodeDib(RgbaImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var xorSize = w * h * 4;
        var maskStride = ((w + 31) / 32) * 4;
        var maskSize = maskStride * h;
        var buffer = new byte[BitmapInfoHeaderSize + xorSize + maskSize];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], h * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);   // planes
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);  // bit count
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0);   // BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(xorSize + maskSize));
        // XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant stay 0.

        var xor = span.Slice(BitmapInfoHeaderSize, xorSize);
        var mask = span.Slice(BitmapInfoHeaderSize + xorSize, maskSize);
        for (var y = 0; y < h; y++)
        {
            var sourceRow = image.Bgra.AsSpan((h - 1 - y) * w * 4, w * 4);
            sourceRow.CopyTo(xor.Slice(y * w * 4, w * 4));
            for (var x = 0; x < w; x++)
            {
                if (sourceRow[(x * 4) + 3] == 0)
                {
                    mask[(y * maskStride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return buffer;
    }
}

/// <summary>Writes uncompressed 24-bit BMP files (BITMAPFILEHEADER + BITMAPINFOHEADER, bottom-up), the format Inno Setup requires.</summary>
internal static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PixelsPerMeter96Dpi = 3780;

    /// <summary>Writes the image as 24-bit RGB. The image must be fully opaque (wizard bitmaps are composed on a solid background).</summary>
    internal static byte[] Write24(RgbaImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var stride = ((w * 3) + 3) & ~3;
        var pixelBytes = stride * h;
        var buffer = new byte[FileHeaderSize + InfoHeaderSize + pixelBytes];
        var span = buffer.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[10..], FileHeaderSize + InfoHeaderSize);

        var info = span[FileHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(info[0..], InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], h);     // positive: bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);   // planes
        BinaryPrimitives.WriteUInt16LittleEndian(info[14..], 24);  // bit count
        BinaryPrimitives.WriteUInt32LittleEndian(info[16..], 0);   // BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(info[20..], (uint)pixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(info[24..], PixelsPerMeter96Dpi);
        BinaryPrimitives.WriteInt32LittleEndian(info[28..], PixelsPerMeter96Dpi);

        var pixels = span[(FileHeaderSize + InfoHeaderSize)..];
        for (var y = 0; y < h; y++)
        {
            var sourceRow = image.Bgra.AsSpan((h - 1 - y) * w * 4, w * 4);
            var targetRow = pixels.Slice(y * stride, stride);
            for (var x = 0; x < w; x++)
            {
                if (sourceRow[(x * 4) + 3] != 255)
                {
                    throw new ArgumentException("24-bit BMP output requires a fully opaque image.", nameof(image));
                }

                targetRow[x * 3] = sourceRow[x * 4];
                targetRow[(x * 3) + 1] = sourceRow[(x * 4) + 1];
                targetRow[(x * 3) + 2] = sourceRow[(x * 4) + 2];
            }
        }

        return buffer;
    }
}
