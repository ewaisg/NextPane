using System.Diagnostics;

namespace NexPane.Architecture.Tests;

/// <summary>
/// TODO.md P1.07: .gitignore must ignore build output but never source folders, branding assets or test fixtures
/// (the repository runs with core.ignorecase=true, so unanchored patterns such as "models/" swallowed src/**/Models/).
/// </summary>
public sealed class GitIgnoreTests
{
    public static TheoryData<string> MustBeTracked() =>
    [
        "assets/icons/NexPane.ico",
        "assets/icons/NexPane-16.png",
        "assets/installer/wizard-164x314.bmp",
        "assets/brand/nexpane-logo.svg",
        "src/NexPane.Core/Models/VfsEntry.cs",
        "src/NexPane.AI/Models/ModelInfo.cs",
        "src/NexPane.Security/Secrets/SecretBuffer.cs",
        "src/NexPane.App/Packages/PackageInfo.cs",
        "src/NexPane.Core/Logs/LogEvent.cs",
        "src/NexPane.Core/Log/LogEvent.cs",
        "src/NexPane.AI/Runtimes/RuntimePack.cs",
        "tools/NexPane.AssetGen/Models/IconSize.cs",
        "tests/TestAssets/content/sample.bak",
        "tests/TestAssets/certs/test.pfx",
        "tests/TestAssets/archives/bin/inner.dat",
        "eng/placeholder-allowlist.json",
    ];

    public static TheoryData<string> MustBeIgnored() =>
    [
        "build/publish/win-x64/NexPane.exe",
        "dist/NexPane-Setup.exe",
        "src/NexPane.Core/bin/Release/NexPane.Core.dll",
        "src/NexPane.Core/obj/project.assets.json",
        "tests/NexPane.Core.Tests/bin/Release/x.dll",
        "tests/NexPane.Benchmarks/obj/x.json",
        "tools/NexPane.AssetGen/bin/Release/x.dll",
        "models/ggml-base.bin",
        "secrets/key.txt",
        "installer/Output/setup.exe",
        "src/NexPane.App/NexPane.App.csproj.user",
        "signing.pfx",
    ];

    [Theory]
    [MemberData(nameof(MustBeTracked))]
    public void Source_assets_and_fixtures_are_not_ignored(string path) =>
        CheckIgnore(path).Should().Be(1, $"'{path}' must be committable (git check-ignore -q exit 1 = not ignored)");

    [Theory]
    [MemberData(nameof(MustBeIgnored))]
    public void Build_output_and_secrets_are_ignored(string path) =>
        CheckIgnore(path).Should().Be(0, $"'{path}' must be ignored (git check-ignore -q exit 0)");

    private static int CheckIgnore(string path)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryModel.Root,
        };
        psi.ArgumentList.Add("check-ignore");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().BeOneOf([0, 1], $"git check-ignore failed: {stderr}");
        return process.ExitCode;
    }
}
