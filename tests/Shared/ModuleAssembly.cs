using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;

namespace NexPane.Tests.Shared;

/// <summary>
/// Reads build facts (PE machine, target framework, target/supported platform) of a NexPane module assembly
/// copied into the test output directory, and asserts them against the rules in ARCHITECTURE.md §2.
/// </summary>
internal static class ModuleAssembly
{
    internal const string ExpectedFramework = ".NETCoreApp,Version=v10.0";
    internal const string ExpectedTargetPlatform = "Windows10.0.19041.0";
    internal const string ExpectedSupportedPlatform = "Windows10.0.17763.0";

    internal sealed record Facts(
        string Path,
        Machine Machine,
        CorFlags CorFlags,
        string? TargetFramework,
        string? TargetPlatform,
        string? SupportedOSPlatform);

    internal static Facts Read(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
        File.Exists(path).Should().BeTrue($"the module '{fileName}' must be copied to the test output via a ProjectReference");

        Machine machine;
        CorFlags corFlags;
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            machine = pe.PEHeaders.CoffHeader.Machine;
            corFlags = pe.PEHeaders.CorHeader!.Flags;
        }

        var assembly = Assembly.LoadFrom(path);
        return new Facts(
            path,
            machine,
            corFlags,
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
            assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName,
            assembly.GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName);
    }

    /// <summary>Asserts a Windows-targeting module: x64, net10.0-windows10.0.19041.0, supported from Windows 10 1809.</summary>
    internal static void AssertWindowsX64Module(string fileName)
    {
        var facts = Read(fileName);
        facts.Machine.Should().Be(Machine.Amd64, "all NexPane binaries are x64-only");
        facts.CorFlags.Should().NotHaveFlag(CorFlags.Requires32Bit);
        facts.TargetFramework.Should().Be(ExpectedFramework);
        facts.TargetPlatform.Should().Be(ExpectedTargetPlatform);
        facts.SupportedOSPlatform.Should().Be(ExpectedSupportedPlatform);
    }

    /// <summary>Asserts the platform-neutral domain module (NexPane.Core): x64, plain net10.0, no Windows platform attribute.</summary>
    internal static void AssertPortableX64Module(string fileName)
    {
        var facts = Read(fileName);
        facts.Machine.Should().Be(Machine.Amd64, "all NexPane binaries are x64-only");
        facts.TargetFramework.Should().Be(ExpectedFramework);
        facts.TargetPlatform.Should().BeNull("NexPane.Core must not depend on Windows APIs");
    }
}
