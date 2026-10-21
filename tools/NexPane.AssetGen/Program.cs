using System.Globalization;

namespace NexPane.AssetGen;

/// <summary>
/// Command-line entry point of the branding asset generator (task P2.02, ARCHITECTURE section 19.1).
/// <code>dotnet run -c Release --project tools/NexPane.AssetGen -- --svg assets/brand/nexpane-logo.svg --mark assets/brand/nexpane-mark.svg --out assets</code>
/// </summary>
internal static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitFailed = 1;
    internal const int ExitUsage = 2;

    private const string Usage =
        "Usage: NexPane.AssetGen --svg <logo.svg> --mark <mark.svg> --out <assets dir> [--preview <sheet.png>]\n" +
        "  --svg      Logo SVG with nested <svg id=\"mark\"> (detailed mark, icon sizes >= 40 px) and <svg id=\"wordmark\">.\n" +
        "  --mark     Simplified square mark SVG for icon sizes <= 32 px.\n" +
        "  --out      Output folder; writes icons\\*.ico, icons\\*.png and installer\\*.bmp below it.\n" +
        "  --preview  Optional review sheet: every icon layer at 1x and enlarged 8x on light and dark backgrounds.";

    internal static int Main(string[] args)
    {
        if (!CommandLine.TryParse(args, out var options, out var error))
        {
            if (error is not null)
            {
                Console.Error.WriteLine("ERROR: " + error);
            }

            Console.Error.WriteLine(Usage);
            return error is null ? ExitOk : ExitUsage;
        }

        try
        {
            var brand = BrandSources.Load(options.LogoSvgPath, options.MarkSvgPath);
            var written = AssetPipeline.Generate(brand, options.OutputDirectory);
            foreach (var file in written)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  wrote {file.RelativePath} ({file.Length:N0} bytes)"));
            }

            if (options.PreviewPath is not null)
            {
                PreviewSheet.Write(brand, options.PreviewPath);
                Console.WriteLine("  wrote preview " + options.PreviewPath);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"AssetGen: {written.Count} files written to {Path.GetFullPath(options.OutputDirectory)}"));
            return ExitOk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return ExitFailed;
        }
    }
}

/// <summary>Parsed command-line options.</summary>
internal sealed record AssetGenOptions(string LogoSvgPath, string MarkSvgPath, string OutputDirectory, string? PreviewPath);

/// <summary>Minimal, strict parser for the AssetGen switches.</summary>
internal static class CommandLine
{
    /// <summary>Parses <paramref name="args"/>. Returns false with a null error for --help, or with an error message.</summary>
    internal static bool TryParse(IReadOnlyList<string> args, out AssetGenOptions options, out string? error)
    {
        options = new AssetGenOptions(string.Empty, string.Empty, string.Empty, null);
        string? svg = null, mark = null, output = null, preview = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "-h" or "--help" or "/?")
            {
                error = null;
                return false;
            }

            if (i + 1 >= args.Count)
            {
                error = $"Missing value for '{name}'.";
                return false;
            }

            var value = args[++i];
            switch (name)
            {
                case "--svg": svg = value; break;
                case "--mark": mark = value; break;
                case "--out": output = value; break;
                case "--preview": preview = value; break;
                default:
                    error = $"Unknown option '{name}'.";
                    return false;
            }
        }

        if (svg is null || mark is null || output is null)
        {
            error = "--svg, --mark and --out are required.";
            return false;
        }

        options = new AssetGenOptions(svg, mark, output, preview);
        error = null;
        return true;
    }
}
