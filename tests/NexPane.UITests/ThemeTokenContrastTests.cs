using System.Globalization;
using System.Xml.Linq;

namespace NexPane.UITests;

/// <summary>task P2.08: the logo-derived theme palette meets WCAG contrast in both themes.</summary>
public sealed class ThemeTokenContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Lazy<IReadOnlyDictionary<string, (byte R, byte G, byte B)>> Tokens = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
        {
            dir = dir.Parent;
        }

        var doc = XDocument.Load(Path.Combine(dir!.FullName, "src", "NexPane.App", "Themes", "Tokens.xaml"));
        return doc.Root!.Elements().Where(e => e.Name.LocalName == "Color").ToDictionary(
            e => (string)e.Attribute(X + "Key")!,
            e =>
            {
                var hex = e.Value.Trim().TrimStart('#');
                hex.Length.Should().Be(8, "tokens are #AARRGGBB");
                hex[..2].Should().Be("FF", "theme tokens are opaque");
                var rgb = uint.Parse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            });
    });

    public static TheoryData<string, string, string, double> Pairs()
    {
        var data = new TheoryData<string, string, string, double>();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            foreach (var surface in new[] { "Surface", "SurfaceAlt" })
            {
                data.Add(theme, "Text", surface, 4.5);
                data.Add(theme, "TextSecondary", surface, 4.5);
                data.Add(theme, "Accent", surface, 3.0);
            }

            data.Add(theme, "AccentForeground", "Accent", 4.5);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Token_pair_meets_the_contrast_minimum(string theme, string foreground, string background, double minimum)
    {
        var fg = Tokens.Value[$"Color.{theme}.{foreground}"];
        var bg = Tokens.Value[$"Color.{theme}.{background}"];
        Contrast(fg, bg).Should().BeGreaterThanOrEqualTo(minimum, $"{theme}: {foreground} on {background}");
    }

    [Fact]
    public void Brand_tokens_match_the_logo()
    {
        Tokens.Value["Color.Brand.Sky"].Should().Be(((byte)0x1F, (byte)0xB6, (byte)0xFF));
        Tokens.Value["Color.Brand.Indigo"].Should().Be(((byte)0x43, (byte)0x38, (byte)0xF5));
        Tokens.Value["Color.Brand.Spark"].Should().Be(((byte)0xFF, (byte)0xC2, (byte)0x4B));
    }

    private static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double Luminance((byte R, byte G, byte B) c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
