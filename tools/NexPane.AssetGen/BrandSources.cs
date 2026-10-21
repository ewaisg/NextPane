using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NexPane.AssetGen;

/// <summary>
/// The two hand-authored brand SVGs and the documents derived from them (ARCHITECTURE section 19.1):
/// the simplified mark (16-32 px), the detailed mark and the wordmark (both nested in the logo),
/// and the single-colour tray glyph.
/// </summary>
internal sealed class BrandSources
{
    internal static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>Elements that would pull in fonts, raster images, scripts or other external content.</summary>
    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.Ordinal)
    {
        "text", "tspan", "textPath", "font", "font-face", "image", "script", "foreignObject", "style", "use",
    };

    private BrandSources(XDocument logo, XDocument mark)
    {
        Logo = logo;
        Mark = mark;
    }

    /// <summary>The full logo document (mark above wordmark).</summary>
    internal XDocument Logo { get; }

    /// <summary>The simplified square mark document.</summary>
    internal XDocument Mark { get; }

    /// <summary>Simplified mark, used for icon layers of <see cref="AssetPipeline.SimplifiedMarkMaxSize"/> px and below.</summary>
    internal string SimplifiedMarkSvg => Serialize(Mark);

    /// <summary>Detailed mark extracted from the logo's nested <c>&lt;svg id="mark"&gt;</c>.</summary>
    internal string DetailedMarkSvg => Serialize(ExtractNested(Logo, "mark"));

    /// <summary>The wordmark extracted from the logo's nested <c>&lt;svg id="wordmark"&gt;</c>, recoloured to <paramref name="color"/>.</summary>
    internal string WordmarkSvg(string color)
    {
        var doc = ExtractNested(Logo, "wordmark");
        Recolor(doc.Root!, color);
        return Serialize(doc);
    }

    /// <summary>
    /// Single-colour tray glyph: the "N" of the simplified mark only (no tile, no spark: in one colour the spark reads
    /// as the dot of an "i"), every shape filled with <paramref name="color"/>. The glyph stays on the 16-px grid.
    /// </summary>
    internal string TrayGlyphSvg(string color)
    {
        var doc = new XDocument(Mark);
        var root = doc.Root!;
        root.Descendants().Where(e => (string?)e.Attribute("id") is "tile" or "spark").ToList().ForEach(e => e.Remove());
        root.Elements(SvgNs + "defs").Remove();
        Recolor(root, color);
        return Serialize(doc);
    }

    /// <summary>Loads and validates both brand SVGs.</summary>
    internal static BrandSources Load(string logoSvgPath, string markSvgPath)
    {
        var logo = LoadValidated(logoSvgPath);
        var mark = LoadValidated(markSvgPath);
        _ = ExtractNested(logo, "mark");
        _ = ExtractNested(logo, "wordmark");
        return new BrandSources(logo, mark);
    }

    /// <summary>Parses an SVG file and enforces the P2.01 rules: SVG 1.1 root, square viewBox, no text/fonts/external references.</summary>
    internal static XDocument LoadValidated(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"SVG file not found: {Path.GetFullPath(path)}", path);
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{path}: not well-formed XML: {ex.Message}", ex);
        }

        var root = doc.Root;
        if (root is null || root.Name != SvgNs + "svg")
        {
            throw new InvalidDataException($"{path}: the root element must be <svg> in the {SvgNs} namespace.");
        }

        if ((string?)root.Attribute("version") != "1.1")
        {
            throw new InvalidDataException($"{path}: the root <svg> must declare version=\"1.1\".");
        }

        var viewBox = ParseViewBox(root, path);
        if (Math.Abs(viewBox.Width - viewBox.Height) > 1e-6)
        {
            throw new InvalidDataException($"{path}: the viewBox must be square (found {viewBox.Width}x{viewBox.Height}).");
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.Namespace != SvgNs)
            {
                throw new InvalidDataException($"{path}: element <{element.Name}> is outside the SVG namespace.");
            }

            if (ForbiddenElements.Contains(element.Name.LocalName))
            {
                throw new InvalidDataException($"{path}: <{element.Name.LocalName}> is not allowed; convert text to paths and inline every shape.");
            }

            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;
                var isHref = attribute.Name.LocalName == "href" && (attribute.Name.Namespace == XLinkNs || attribute.Name.Namespace == XNamespace.None);
                if (isHref && !value.StartsWith('#'))
                {
                    throw new InvalidDataException($"{path}: external reference '{value}' is not allowed.");
                }

                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) && !value.Contains("url(#", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{path}: external url() reference '{value}' is not allowed.");
                }

                if (attribute.Name.LocalName is "font-family" or "font")
                {
                    throw new InvalidDataException($"{path}: font attributes are not allowed; convert text to paths.");
                }
            }
        }

        return doc;
    }

    /// <summary>Parses the <c>viewBox</c> of an <c>&lt;svg&gt;</c> element.</summary>
    internal static (double X, double Y, double Width, double Height) ParseViewBox(XElement svg, string context)
    {
        var raw = (string?)svg.Attribute("viewBox")
            ?? throw new InvalidDataException($"{context}: <svg> has no viewBox.");
        var parts = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || w <= 0 || h <= 0)
        {
            throw new InvalidDataException($"{context}: invalid viewBox '{raw}'.");
        }

        return (x, y, w, h);
    }

    /// <summary>
    /// Turns a nested <c>&lt;svg id="..."&gt;</c> of <paramref name="source"/> into a standalone document whose size equals
    /// its viewBox. The root <c>&lt;defs&gt;</c> are copied so gradient references keep resolving.
    /// </summary>
    internal static XDocument ExtractNested(XDocument source, string id)
    {
        var nested = source.Root!.Elements(SvgNs + "svg").SingleOrDefault(e => (string?)e.Attribute("id") == id)
            ?? throw new InvalidDataException($"The logo SVG has no top-level nested <svg id=\"{id}\">.");
        var viewBox = ParseViewBox(nested, $"<svg id=\"{id}\">");

        var root = new XElement(SvgNs + "svg",
            new XAttribute("version", "1.1"),
            new XAttribute("width", viewBox.Width.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("height", viewBox.Height.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("viewBox", (string)nested.Attribute("viewBox")!));
        root.Add(source.Root!.Elements(SvgNs + "defs").Select(d => new XElement(d)));
        root.Add(nested.Elements().Select(e => new XElement(e)));
        return new XDocument(root);
    }

    /// <summary>Overrides every fill and stroke colour below <paramref name="root"/> (gradients and "none" are replaced or kept accordingly).</summary>
    private static void Recolor(XElement root, string color)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var name in new[] { "fill", "stroke" })
            {
                var attribute = element.Attribute(name);
                if (attribute is not null && attribute.Value != "none")
                {
                    attribute.Value = color;
                }
            }

            element.Attribute("fill-opacity")?.Remove();
        }

        if (root.Attribute("fill") is null)
        {
            root.SetAttributeValue("fill", color);
        }
    }

    /// <summary>Serializes a document without an XML declaration, with stable formatting.</summary>
    internal static string Serialize(XDocument doc) => doc.Root!.ToString(SaveOptions.DisableFormatting);
}
