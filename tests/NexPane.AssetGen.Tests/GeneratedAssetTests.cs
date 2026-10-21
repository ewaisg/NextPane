using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.Versioning;

namespace NexPane.AssetGen.Tests;

/// <summary>task P2.03-P2.06: the committed branding outputs under assets/ have the required sizes and formats.</summary>
[SupportedOSPlatform("windows")]
public sealed class GeneratedAssetTests
{
    private static readonly int[] RequiredAppIconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly int[] RequiredTraySizes = [16, 20, 24, 32, 40, 48];

    public static TheoryData<int> AppSizes() => new(RequiredAppIconSizes);

    [Fact]
    public void App_icon_contains_every_required_layer()
    {
        var entries = AssetFiles.ReadIco(File.ReadAllBytes(Path.Combine(AssetFiles.Assets, "icons", "NexPane.ico")));
        entries.Select(e => e.Width).Should().BeEquivalentTo(RequiredAppIconSizes, o => o.WithStrictOrdering());
        entries.Should().NotContain(e => e.Width != e.Height || e.BitCount != 32, "every layer is square and 32 bpp");
        entries.Single(e => e.Width == 256).IsPng.Should().BeTrue("the 256 px layer is PNG-compressed (P2.04)");
        foreach (var dib in entries.Where(e => e.Width <= 64))
        {
            dib.IsPng.Should().BeFalse($"{dib.Width} px layer is a 32-bpp DIB for older loaders (ARCHITECTURE 19.1)");
            var header = dib.Data.AsSpan();
            BinaryPrimitives.ReadInt32LittleEndian(header).Should().Be(40);
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]).Should().Be(dib.Width);
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]).Should().Be(dib.Width * 2, "XOR + AND mask height");
            BinaryPrimitives.ReadUInt16LittleEndian(header[14..]).Should().Be(32);
            var maskStride = ((dib.Width + 31) / 32) * 4;
            dib.Data.Length.Should().Be(40 + (dib.Width * dib.Width * 4) + (maskStride * dib.Width));
        }
    }

    [Theory]
    [MemberData(nameof(AppSizes))]
    public void System_Drawing_loads_and_decodes_every_app_icon_size(int size)
    {
        var path = Path.Combine(AssetFiles.Assets, "icons", "NexPane.ico");
        using var icon = new Icon(path, new Size(size, size));
        if (size == 256)
        {
            // System.Drawing.Icon's own frame selection skips PNG-compressed entries, so the 256 px PNG layer is
            // decoded directly and also loaded through the Windows icon loader (LoadImage), which Explorer uses.
            var png = AssetFiles.ReadIco(File.ReadAllBytes(path)).Single(e => e.Width == 256);
            using var pngBitmap = new Bitmap(new MemoryStream(png.Data));
            pngBitmap.Width.Should().Be(256);
            pngBitmap.PixelFormat.Should().Be(System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }
        else
        {
            icon.Width.Should().Be(size);
            icon.Height.Should().Be(size);
        }

        var handle = NativeIcons.LoadImage(0, path, NativeIcons.ImageIcon, size, size, NativeIcons.LoadFromFile);
        handle.Should().NotBe(0, $"Windows LoadImage must load the {size} px layer");
        try
        {
            using var native = Icon.FromHandle(handle);
            native.Width.Should().Be(size);
        }
        finally
        {
            NativeIcons.DestroyIcon(handle);
        }

        using var bitmap = size == 256
            ? new Bitmap(new MemoryStream(AssetFiles.ReadIco(File.ReadAllBytes(path)).Single(e => e.Width == 256).Data))
            : icon.ToBitmap();
        bitmap.Width.Should().Be(size);
        bitmap.Height.Should().Be(size);
        var centre = bitmap.GetPixel(size / 2, size / 2);
        centre.A.Should().Be(255, "the centre of the mark is opaque");
        bitmap.GetPixel(0, size - 1).A.Should().BeLessThan(255, "the rounded tile leaves the corner (partly) transparent");
    }

    [Theory]
    [MemberData(nameof(AppSizes))]
    public void Png_export_has_the_size_and_32_bpp_alpha(int size)
    {
        var header = AssetFiles.ReadPngHeader(File.ReadAllBytes(Path.Combine(AssetFiles.Assets, "icons", $"NexPane-{size}.png")));
        header.Width.Should().Be(size);
        header.Height.Should().Be(size);
        header.BitDepth.Should().Be(8);
        header.ColorType.Should().Be(6, "colour type 6 = RGBA, i.e. 32 bpp with alpha");
    }

    [Theory]
    [InlineData("tray.ico")]
    [InlineData("tray-light.ico")]
    [InlineData("tray-dark.ico")]
    public void Tray_icons_contain_the_tray_sizes(string file)
    {
        var entries = AssetFiles.ReadIco(File.ReadAllBytes(Path.Combine(AssetFiles.Assets, "icons", file)));
        entries.Select(e => e.Width).Should().BeEquivalentTo(RequiredTraySizes, o => o.WithStrictOrdering());
        foreach (var size in RequiredTraySizes)
        {
            using var icon = new Icon(Path.Combine(AssetFiles.Assets, "icons", file), new Size(size, size));
            icon.Width.Should().Be(size);
        }
    }

    [Theory]
    [InlineData("tray-light.ico", "#F3F3F3")] // Windows 11 light taskbar
    [InlineData("tray-light.ico", "#FFFFFF")]
    [InlineData("tray-dark.ico", "#202020")]  // Windows 11 dark taskbar
    [InlineData("tray-dark.ico", "#000000")]  // Windows 10 dark taskbar
    public void Tray_glyph_contrasts_with_the_taskbar_by_at_least_3_to_1(string file, string taskbar)
    {
        var background = ColorTranslator.FromHtml(taskbar);
        var backgroundLuminance = AssetFiles.Luminance(background.R, background.G, background.B);
        foreach (var entry in AssetFiles.ReadIco(File.ReadAllBytes(Path.Combine(AssetFiles.Assets, "icons", file))))
        {
            var w = entry.Width;
            var pixels = entry.Data.AsSpan(40, w * w * 4);
            var glyphPixels = 0;
            var worst = double.MaxValue;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 255)
                {
                    glyphPixels++;
                    worst = Math.Min(worst, AssetFiles.Contrast(AssetFiles.Luminance(pixels[i + 2], pixels[i + 1], pixels[i]), backgroundLuminance));
                }
            }

            glyphPixels.Should().BeGreaterThan(w * w / 8, $"{file} {w}px must draw a visible glyph");
            worst.Should().BeGreaterThanOrEqualTo(3.0, $"{file} {w}px on {taskbar}");
        }
    }

    public static TheoryData<int, int> WizardSizes() => new() { { 164, 314 }, { 205, 393 }, { 246, 471 }, { 287, 550 }, { 328, 628 } };

    [Theory]
    [MemberData(nameof(WizardSizes))]
    public void Wizard_image_is_a_24_bit_bmp_of_the_exact_size(int width, int height) =>
        AssertBmp($"wizard-{width}x{height}.bmp", width, height);

    [Theory]
    [InlineData(55)]
    [InlineData(69)]
    [InlineData(83)]
    [InlineData(97)]
    [InlineData(110)]
    public void Wizard_small_image_is_a_24_bit_bmp_of_the_exact_size(int size) =>
        AssertBmp($"wizard-small-{size}.bmp", size, size);

    private static void AssertBmp(string file, int width, int height)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetFiles.Assets, "installer", file));
        var header = AssetFiles.ReadBmpHeader(bytes);
        header.Width.Should().Be(width);
        header.Height.Should().Be(height, "bottom-up DIB with the exact height");
        header.BitCount.Should().Be(24, "Inno Setup wizard images are 24-bit BMPs");
        header.Compression.Should().Be(0, "BI_RGB");
        header.FileSize.Should().Be(bytes.Length);
        header.PixelOffset.Should().Be(54);
        bytes.Length.Should().Be(54 + ((((width * 3) + 3) & ~3) * height));
        using var bitmap = new Bitmap(Path.Combine(AssetFiles.Assets, "installer", file));
        bitmap.Width.Should().Be(width);
        bitmap.Height.Should().Be(height);
    }
}
