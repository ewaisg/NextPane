using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Svg.Skia;

namespace NexPane.AssetGen;

/// <summary>A rendered image as straight (non-premultiplied) BGRA pixels, top-down rows, 4 bytes per pixel.</summary>
internal sealed class RgbaImage
{
    internal RgbaImage(int width, int height, byte[] bgra)
    {
        if (bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Pixel buffer size does not match the image dimensions.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Straight-alpha BGRA bytes, rows top-down.</summary>
    internal byte[] Bgra { get; }

    /// <summary>Encodes the image as an 8-bit RGBA PNG (colour type 6). Skia's PNG encoder writes no timestamps, so output is deterministic.</summary>
    internal byte[] EncodePng()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(Bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            var options = new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9);
            using var data = pixmap.Encode(options)
                ?? throw new InvalidDataException("Skia failed to encode a PNG.");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>Renders SVG documents with Svg.Skia into Skia canvases and pixel buffers.</summary>
internal static class SvgRasterizer
{
    /// <summary>Renders <paramref name="svgText"/> into a transparent square of <paramref name="size"/> px.</summary>
    internal static RgbaImage RenderSquare(string svgText, int size) =>
        Render(size, size, canvas => Draw(canvas, svgText, SKRect.Create(0, 0, size, size)));

    /// <summary>Creates a premultiplied surface, lets <paramref name="paint"/> draw on it, and returns straight-alpha pixels.</summary>
    internal static RgbaImage Render(int width, int height, Action<SKCanvas> paint)
    {
        var premulInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(premulInfo);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            paint(canvas);
            canvas.Flush();
        }

        var straightInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pixels = new byte[straightInfo.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var source = bitmap.PeekPixels();
            using var target = new SKPixmap(straightInfo, handle.AddrOfPinnedObject(), straightInfo.RowBytes);
            if (!source.ReadPixels(target))
            {
                throw new InvalidDataException("Skia failed to convert premultiplied pixels to straight alpha.");
            }
        }
        finally
        {
            handle.Free();
        }

        return new RgbaImage(width, height, pixels);
    }

    /// <summary>
    /// Draws <paramref name="svgText"/> scaled uniformly to fit <paramref name="destination"/> and centred in it.
    /// </summary>
    internal static void Draw(SKCanvas canvas, string svgText, SKRect destination)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgText));
        var picture = svg.Load(stream)
            ?? throw new InvalidDataException("Svg.Skia could not load the SVG document.");
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("The SVG document has an empty drawing area.");
        }

        var scale = Math.Min(destination.Width / bounds.Width, destination.Height / bounds.Height);
        var offsetX = destination.Left + ((destination.Width - (bounds.Width * scale)) / 2f);
        var offsetY = destination.Top + ((destination.Height - (bounds.Height * scale)) / 2f);

        canvas.Save();
        canvas.Translate(offsetX, offsetY);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }
}
