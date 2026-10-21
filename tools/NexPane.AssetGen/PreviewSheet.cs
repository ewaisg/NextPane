using System.Runtime.InteropServices;
using SkiaSharp;

namespace NexPane.AssetGen;

/// <summary>
/// Review sheet for the manual icon review required by P2.01/P2.09: every app icon layer at 1x and enlarged
/// with nearest-neighbour sampling (so individual pixels are visible) on light and dark backgrounds, the tray
/// variants on the taskbar colours they are meant for, the full logo, and the installer bitmaps.
/// </summary>
internal static class PreviewSheet
{
    private const int Margin = 24;
    private const int Zoom = 8;
    private static readonly SKColor Light = SKColor.Parse("#F3F3F3");
    private static readonly SKColor Dark = SKColor.Parse("#202020");

    internal static void Write(BrandSources brand, string path)
    {
        var simplified = brand.SimplifiedMarkSvg;
        var detailed = brand.DetailedMarkSvg;
        var layers = AssetPipeline.AppIconSizes.Select(s => AssetPipeline.RenderMark(simplified, detailed, s)).ToList();
        var trayLight = AssetPipeline.TrayIconSizes.Select(s => SvgRasterizer.RenderSquare(brand.TrayGlyphSvg(BrandPalette.TrayGlyphOnLight), s)).ToList();
        var trayDark = AssetPipeline.TrayIconSizes.Select(s => SvgRasterizer.RenderSquare(brand.TrayGlyphSvg(BrandPalette.TrayGlyphOnDark), s)).ToList();
        var (ww, wh) = AssetPipeline.WizardImageSizes[0];
        var wizard = AssetPipeline.RenderWizardImage(detailed, brand.WordmarkSvg("#FFFFFF"), ww, wh);
        var wizardSmall = AssetPipeline.RenderWizardSmallImage(detailed, AssetPipeline.WizardSmallImageSizes[0]);

        const int width = 2 * (Margin + (48 * Zoom)) + 1400;
        const int height = 1500;
        var sheet = SvgRasterizer.Render(width, height, canvas =>
        {
            canvas.Clear(SKColors.White);
            var half = width / 2;
            Fill(canvas, SKRect.Create(0, 0, half, height), Light);
            Fill(canvas, SKRect.Create(half, 0, width - half, height), Dark);

            foreach (var (originX, tray) in new[] { (0, trayLight), (half, trayDark) })
            {
                // Row 1: every app layer at 1x.
                var x = originX + Margin;
                foreach (var layer in layers)
                {
                    Blit(canvas, layer, x, Margin, 1);
                    x += layer.Width + 12;
                }

                // Row 2: 16-48 px app layers enlarged.
                x = originX + Margin;
                var y = Margin + 256 + Margin;
                foreach (var layer in layers.Where(l => l.Width <= 48))
                {
                    Blit(canvas, layer, x, y, Zoom);
                    x += (layer.Width * Zoom) + 12;
                    if (x > originX + half - (48 * Zoom))
                    {
                        x = originX + Margin;
                        y += (32 * Zoom) + 12;
                    }
                }

                // Row 3: tray glyphs at 1x and enlarged.
                y = 900;
                x = originX + Margin;
                foreach (var glyph in tray)
                {
                    Blit(canvas, glyph, x, y, 1);
                    x += glyph.Width + 12;
                }

                x = originX + Margin;
                foreach (var glyph in tray.Where(g => g.Width is 16 or 24 or 32))
                {
                    Blit(canvas, glyph, x, y + 60, Zoom);
                    x += (glyph.Width * Zoom) + 12;
                }

                // Row 4: logo.
                SvgRasterizer.Draw(canvas, BrandSources.Serialize(brand.Logo), SKRect.Create(originX + Margin, 1180, 300, 300));
            }

            Blit(canvas, wizard, half - ww - Margin, 1180, 1);
            Blit(canvas, wizardSmall, half - ww - Margin - 80, 1180, 1);
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, sheet.EncodePng());
    }

    private static void Fill(SKCanvas canvas, SKRect rect, SKColor color)
    {
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(rect, paint);
    }

    private static void Blit(SKCanvas canvas, RgbaImage image, float x, float y, int zoom)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var skImage = SKImage.FromPixelCopy(pixmap);
            canvas.DrawImage(skImage, SKRect.Create(x, y, image.Width * zoom, image.Height * zoom), new SKSamplingOptions(SKFilterMode.Nearest));
        }
        finally
        {
            handle.Free();
        }
    }
}
