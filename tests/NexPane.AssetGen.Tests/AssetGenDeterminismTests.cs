using System.Security.Cryptography;
using NexPane.Tests.Shared;

namespace NexPane.AssetGen.Tests;

/// <summary>task P2.02: the CLI produces every output with exit code 0, and a second run is byte-identical (SHA-256).</summary>
public sealed class AssetGenDeterminismTests
{
    private static readonly string[] ExpectedFiles =
    [
        .. new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }.Select(s => Path.Combine("icons", $"NexPane-{s}.png")),
        Path.Combine("icons", "NexPane.ico"),
        Path.Combine("icons", "tray.ico"),
        Path.Combine("icons", "tray-light.ico"),
        Path.Combine("icons", "tray-dark.ico"),
        .. new[] { "164x314", "205x393", "246x471", "287x550", "328x628" }.Select(s => Path.Combine("installer", $"wizard-{s}.bmp")),
        .. new[] { 55, 69, 83, 97, 110 }.Select(s => Path.Combine("installer", $"wizard-small-{s}.bmp")),
    ];

    [Fact]
    public void Two_cli_runs_produce_every_file_with_identical_sha256()
    {
        var first = Directory.CreateTempSubdirectory("npag-a-");
        var second = Directory.CreateTempSubdirectory("npag-b-");
        try
        {
            RunCli(first.FullName).Should().Be(0);
            RunCli(second.FullName).Should().Be(0);

            var a = HashTree(first.FullName);
            var b = HashTree(second.FullName);
            a.Keys.Should().BeEquivalentTo(ExpectedFiles, "the generator writes exactly the P2.03-P2.06 outputs");
            b.Should().BeEquivalentTo(a, "a second run must yield byte-identical outputs");
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void Committed_assets_match_a_fresh_generation()
    {
        var fresh = Directory.CreateTempSubdirectory("npag-c-");
        try
        {
            RunCli(fresh.FullName).Should().Be(0);
            var generated = HashTree(fresh.FullName);
            foreach (var (relative, hash) in generated)
            {
                var committed = Path.Combine(AssetFiles.Assets, relative);
                File.Exists(committed).Should().BeTrue($"{relative} must be committed under assets/");
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(committed))).Should().Be(hash,
                    $"assets/{relative} is stale; re-run tools/NexPane.AssetGen (HANDOFF section 5)");
            }
        }
        finally
        {
            fresh.Delete(recursive: true);
        }
    }

    [Fact]
    public void Cli_rejects_missing_arguments_with_usage_exit_code()
    {
        Program.Main(["--svg", AssetFiles.LogoSvg]).Should().Be(Program.ExitUsage);
        Program.Main(["--bogus", "x"]).Should().Be(Program.ExitUsage);
        Program.Main(["--help"]).Should().Be(Program.ExitOk);
    }

    [Fact]
    public void Cli_reports_a_missing_svg_as_failure()
    {
        var output = Directory.CreateTempSubdirectory("npag-d-");
        try
        {
            Program.Main(["--svg", Path.Combine(output.FullName, "absent.svg"), "--mark", AssetFiles.MarkSvg, "--out", output.FullName])
                .Should().Be(Program.ExitFailed);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public void Tool_is_built_x64_for_the_architecture_target_framework()
    {
        ModuleAssembly.AssertWindowsX64Module("NexPane.AssetGen.dll");
    }

    private static int RunCli(string outputDirectory) =>
        Program.Main(["--svg", AssetFiles.LogoSvg, "--mark", AssetFiles.MarkSvg, "--out", outputDirectory]);

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);
}
