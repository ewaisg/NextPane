using System.Globalization;
using SkiaSharp;

namespace NexPane.AssetGen;

/// <summary>The NexPane brand colours used by the generator (the SVGs carry the same values).</summary>
internal static class BrandPalette
{
    /// <summary>Tile gradient start (top-left), sky blue.</summary>
    internal const string TileStart = "#1FB6FF";

    /// <summary>Tile gradient end (bottom-right), indigo.</summary>
    internal const string TileEnd = "#4338F5";

    /// <summary>Accent spark, amber.</summary>
    internal const string Spark = "#FFC24B";

    /// <summary>Wordmark colour; mid-luminance blue that reads on both white and near-black backgrounds.</summary>
    internal const string Wordmark = "#4D74FF";

    /// <summary>Tray glyph for light taskbars.</summary>
    internal const string TrayGlyphOnLight = "#1A1F36";

    /// <summary>Tray glyph for dark taskbars.</summary>
    internal const string TrayGlyphOnDark = "#FFFFFF";

    /// <summary>Installer welcome-image background, top.</summary>
    internal const string WizardTop = "#0D1638";

    /// <summary>Installer welcome-image background, bottom.</summary>
    internal const string WizardBottom = "#262B8F";

    /// <summary>Installer header (small image) background; Inno's modern wizard header is white.</summary>
    internal const string WizardSmallBackground = "#FFFFFF";

    internal static SKColor Parse(string hex) => SKColor.Parse(hex);
}

/// <summary>A file written by the pipeline.</summary>
internal sealed record WrittenAsset(string RelativePath, long Length);

/// <summary>Produces every branding output of ARCHITECTURE section 19.1 / task P2.03-P2.06 from the two brand SVGs.</summary>
internal static class AssetPipeline
{
    /// <summary>App icon and PNG export sizes (P2.03, P2.04).</summary>
    internal static readonly int[] AppIconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Tray icon sizes (P2.05).</summary>
    internal static readonly int[] TrayIconSizes = [16, 20, 24, 32, 40, 48];

    /// <summary>Largest size rendered from the simplified mark; larger sizes use the detailed mark.</summary>
    internal const int SimplifiedMarkMaxSize = 32;

    /// <summary>Largest icon layer stored as a 32-bpp DIB; larger layers are PNG-compressed (256 px).</summary>
    internal const int LargestDibLayer = 128;

    /// <summary>Inno Setup <c>WizardImageFile</c> sizes, 100 % to 200 % DPI (P2.06).</summary>
    internal static readonly (int Width, int Height)[] WizardImageSizes = [(164, 314), (205, 393), (246, 471), (287, 550), (328, 628)];

    /// <summary>Inno Setup <c>WizardSmallImageFile</c> sizes, 100 % to 200 % DPI (P2.06).</summary>
    internal static readonly int[] WizardSmallImageSizes = [55, 69, 83, 97, 110];

    internal static string AppIconPath => Path.Combine("icons", "NexPane.ico");

    internal static string AppPngPath(int size) => Path.Combine("icons", string.Create(CultureInfo.InvariantCulture, $"NexPane-{size}.png"));

    internal static string TrayIconPath(string? variant) => Path.Combine("icons", variant is null ? "tray.ico" : $"tray-{variant}.ico");

    internal static string WizardImagePath(int width, int height) => Path.Combine("installer", string.Create(CultureInfo.InvariantCulture, $"wizard-{width}x{height}.bmp"));

    internal static string WizardSmallImagePath(int size) => Path.Combine("installer", string.Create(CultureInfo.InvariantCulture, $"wizard-small-{size}.bmp"));

    /// <summary>Generates every asset below <paramref name="outputDirectory"/> and returns the files written, in a fixed order.</summary>
    internal static IReadOnlyList<WrittenAsset> Generate(BrandSources brand, string outputDirectory)
    {
        var outputs = Build(brand);
        var written = new List<WrittenAsset>(outputs.Count);
        foreach (var (relativePath, bytes) in outputs)
        {
            var fullPath = Path.Combine(outputDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            written.Add(new WrittenAsset(relativePath, bytes.Length));
        }

        return written;
    }

    /// <summary>Renders every asset in memory, keyed by its path relative to the output folder.</summary>
    internal static IReadOnlyList<(string RelativePath, byte[] Bytes)> Build(BrandSources brand)
    {
        var outputs = new List<(string, byte[])>();
        var simplified = brand.SimplifiedMarkSvg;
        var detailed = brand.DetailedMarkSvg;

        // App icon layers and PNG exports (P2.03, P2.04).
        var appLayers = AppIconSizes.ToDictionary(s => s, s => RenderMark(simplified, detailed, s));
        foreach (var size in AppIconSizes)
        {
            outputs.Add((AppPngPath(size), appLayers[size].EncodePng()));
        }

        outputs.Add((AppIconPath, IcoWriter.Write(AppIconSizes.Select(s => new IconLayer(appLayers[s], s > LargestDibLayer)).ToList())));

        // Tray icons (P2.05): full colour, plus single-colour glyphs for light and dark taskbars.
        outputs.Add((TrayIconPath(null), IcoWriter.Write(TrayIconSizes.Select(s => new IconLayer(appLayers[s], false)).ToList())));
        foreach (var (variant, color) in new[] { ("light", BrandPalette.TrayGlyphOnLight), ("dark", BrandPalette.TrayGlyphOnDark) })
        {
            var glyph = brand.TrayGlyphSvg(color);
            outputs.Add((TrayIconPath(variant), IcoWriter.Write(TrayIconSizes.Select(s => new IconLayer(SvgRasterizer.RenderSquare(glyph, s), false)).ToList())));
        }

        // Inno Setup wizard bitmaps (P2.06).
        var wordmark = brand.WordmarkSvg("#FFFFFF");
        foreach (var (w, h) in WizardImageSizes)
        {
            outputs.Add((WizardImagePath(w, h), BmpWriter.Write24(RenderWizardImage(detailed, wordmark, w, h))));
        }

        foreach (var size in WizardSmallImageSizes)
        {
            outputs.Add((WizardSmallImagePath(size), BmpWriter.Write24(RenderWizardSmallImage(detailed, size))));
        }

        return outputs;
    }

    /// <summary>Renders the app mark at <paramref name="size"/> px, choosing the simplified or detailed drawing.</summary>
    internal static RgbaImage RenderMark(string simplifiedSvg, string detailedSvg, int size) =>
        SvgRasterizer.RenderSquare(size <= SimplifiedMarkMaxSize ? simplifiedSvg : detailedSvg, size);

    /// <summary>Welcome/finish page image: deep brand gradient, the mark, and the white wordmark below it.</summary>
    internal static RgbaImage RenderWizardImage(string detailedMarkSvg, string whiteWordmarkSvg, int width, int height) =>
        SvgRasterizer.Render(width, height, canvas =>
        {
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(width * 0.35f, height),
                    [BrandPalette.Parse(BrandPalette.WizardTop), BrandPalette.Parse(BrandPalette.WizardBottom)],
                    SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, width, height, paint);
            }

            var markSize = MathF.Round(width * 0.62f);
            var markTop = MathF.Round(height * 0.24f);
            SvgRasterizer.Draw(canvas, detailedMarkSvg, SKRect.Create((width - markSize) / 2f, markTop, markSize, markSize));

            var wordmarkWidth = width * 0.74f;
            var wordmarkTop = markTop + markSize + (height * 0.05f);
            SvgRasterizer.Draw(canvas, whiteWordmarkSvg, SKRect.Create((width - wordmarkWidth) / 2f, wordmarkTop, wordmarkWidth, wordmarkWidth * 0.25f));
        });

    /// <summary>Wizard header image: the detailed mark on the white header background.</summary>
    internal static RgbaImage RenderWizardSmallImage(string detailedMarkSvg, int size) =>
        SvgRasterizer.Render(size, size, canvas =>
        {
            canvas.Clear(BrandPalette.Parse(BrandPalette.WizardSmallBackground));
            var inset = MathF.Round(size * 0.04f);
            SvgRasterizer.Draw(canvas, detailedMarkSvg, SKRect.Create(inset, inset, size - (2 * inset), size - (2 * inset)));
        });
}
