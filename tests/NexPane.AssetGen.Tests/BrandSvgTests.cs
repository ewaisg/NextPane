using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Svg.Skia;

namespace NexPane.AssetGen.Tests;

/// <summary>task P2.01: the brand SVGs are valid SVG 1.1, square, self-contained, and render with Svg.Skia without warnings.</summary>
public sealed class BrandSvgTests
{
    public static TheoryData<string> BrandSvgs() => new() { "nexpane-logo.svg", "nexpane-mark.svg" };

    [Theory]
    [MemberData(nameof(BrandSvgs))]
    public void Svg_passes_the_generator_validation_rules(string file)
    {
        var doc = BrandSources.LoadValidated(Path.Combine(AssetFiles.Assets, "brand", file));
        var viewBox = BrandSources.ParseViewBox(doc.Root!, file);
        viewBox.Width.Should().Be(viewBox.Height, "P2.01 requires a square viewBox");
        doc.Root!.Attribute("version")!.Value.Should().Be("1.1");
    }

    [Theory]
    [MemberData(nameof(BrandSvgs))]
    public void Svg_has_no_text_fonts_images_or_external_references(string file)
    {
        var text = File.ReadAllText(Path.Combine(AssetFiles.Assets, "brand", file));
        var doc = XDocument.Parse(text);
        var names = doc.Descendants().Select(e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal);
        names.Should().NotContain(["text", "tspan", "font", "font-face", "image", "style", "script", "use"]);
        text.Replace("http://www.w3.org/2000/svg", string.Empty, StringComparison.Ordinal)
            .Should().NotContain("http", "only the SVG namespace URI may appear");
        text.Should().NotContainAny("font-family", "xlink:href", "@import");
    }

    [Theory]
    [MemberData(nameof(BrandSvgs))]
    public void Svg_Skia_loads_the_svg_without_warnings_and_draws_something(string file)
    {
        var listener = new CollectingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var svg = new SKSvg();
            using var stream = File.OpenRead(Path.Combine(AssetFiles.Assets, "brand", file));
            var picture = svg.Load(stream);
            picture.Should().NotBeNull();
            picture!.CullRect.Width.Should().BeGreaterThan(0);
            picture.CullRect.Width.Should().Be(picture.CullRect.Height);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        listener.Messages.ToString().Should().BeEmpty("Svg.Skia must parse the brand SVGs without warnings");
    }

    [Fact]
    public void Logo_contains_the_detailed_mark_and_the_wordmark()
    {
        var brand = BrandSources.Load(AssetFiles.LogoSvg, AssetFiles.MarkSvg);
        brand.DetailedMarkSvg.Should().Contain("id=\"glyph\"").And.Contain("np-tile-gradient");
        brand.WordmarkSvg("#FFFFFF").Should().Contain("#FFFFFF").And.NotContain(BrandPalette.Wordmark);
    }

    [Fact]
    public void Svgs_use_the_generator_palette()
    {
        var logo = File.ReadAllText(AssetFiles.LogoSvg);
        var mark = File.ReadAllText(AssetFiles.MarkSvg);
        foreach (var color in new[] { BrandPalette.TileStart, BrandPalette.TileEnd, BrandPalette.Spark })
        {
            logo.Should().Contain(color);
            mark.Should().Contain(color);
        }

        logo.Should().Contain(BrandPalette.Wordmark);
    }

    [Fact]
    public void Validation_rejects_text_and_external_references()
    {
        var dir = Directory.CreateTempSubdirectory("npag-");
        try
        {
            var withText = Path.Combine(dir.FullName, "text.svg");
            File.WriteAllText(withText, "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 10 10\"><text>N</text></svg>");
            var external = Path.Combine(dir.FullName, "external.svg");
            File.WriteAllText(external, "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 10 10\"><rect width=\"1\" height=\"1\" fill=\"url(http://x/y#g)\"/></svg>");
            var notSquare = Path.Combine(dir.FullName, "wide.svg");
            File.WriteAllText(notSquare, "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 20 10\"/>");

            foreach (var path in new[] { withText, external, notSquare })
            {
                var act = () => BrandSources.LoadValidated(path);
                act.Should().Throw<InvalidDataException>();
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class CollectingTraceListener : TraceListener
    {
        internal StringBuilder Messages { get; } = new();

        public override void Write(string? message) => Messages.Append(message);

        public override void WriteLine(string? message) => Messages.AppendLine(message);
    }
}
