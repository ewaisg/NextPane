using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Mono.Cecil;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using ArchResult = NetArchTest.Rules.TestResult;
using NetArchTest.Rules;

namespace NexPane.Architecture.Tests;

/// <summary>
/// Type-level architecture rules (TODO P3.37, ARCHITECTURE.md §4), evaluated on the compiled module assemblies with
/// NetArchTest.Rules (Mono.Cecil) and System.Reflection.Metadata, plus the project files that decide which frameworks
/// a module can see.
/// </summary>
public sealed class TypeLevelRulesTests
{
    /// <summary>Namespaces of the WPF / WinForms / XAML stacks. Only L3 (App, Preview) may depend on them.</summary>
    private static readonly string[] DesktopUiNamespaces =
    [
        "System.Windows", "System.Xaml", "Microsoft.Windows.Themes", "Microsoft.Win32.OpenFileDialog",
        "Microsoft.Win32.SaveFileDialog", "Microsoft.Win32.OpenFolderDialog",
    ];

    /// <summary>Assemblies that only exist in the Microsoft.WindowsDesktop.App shared framework (WPF + WinForms).</summary>
    private static readonly string[] DesktopUiAssemblies =
    [
        "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows.Forms",
        "System.Windows.Forms.Primitives", "System.Windows.Controls.Ribbon", "System.Windows.Presentation",
        "UIAutomationClient", "UIAutomationTypes", "UIAutomationProvider", "ReachFramework", "DirectWriteForwarder",
        "PresentationUI", "System.Windows.Input.Manipulations",
    ];

    /// <summary>
    /// Platform-interop namespaces that must not appear in NexPane.Core (the portable contracts layer): NexPane.Shell
    /// (all Windows interop lives there), WinRT projections, Win32 handles/registry, COM and source-generated marshalling.
    /// </summary>
    private static readonly string[] InteropNamespaces =
    [
        "NexPane.Shell", "Windows", "WinRT", "Microsoft.Win32", "System.Runtime.InteropServices.ComTypes",
        "System.Runtime.InteropServices.Marshalling", "Windows.Win32",
    ];

    public static TheoryData<string> ModulesBelowPresentation()
    {
        var data = new TheoryData<string>();
        foreach (var module in ModuleAssemblies.BelowPresentation)
        {
            data.Add(module);
        }

        return data;
    }

    [Fact]
    public void Every_module_below_presentation_is_available_for_type_level_checks()
    {
        foreach (var module in ModuleAssemblies.BelowPresentation)
        {
            File.Exists(ModuleAssemblies.PathOf(module)).Should().BeTrue(
                $"{module}.dll must be copied next to the architecture tests (ProjectReference in NexPane.Architecture.Tests.csproj)");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rule 1: no System.Windows* (WPF / WinForms / XAML) below L3.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ModulesBelowPresentation))]
    public void Types_below_presentation_do_not_use_WPF_or_WinForms(string module)
    {
        var result = Types.FromFile(ModuleAssemblies.PathOf(module))
            .ShouldNot().HaveDependencyOnAny(DesktopUiNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{module} is below the presentation layer; only NexPane.App and NexPane.Preview may use WPF/WinForms types. Offending types: {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(ModulesBelowPresentation))]
    public void Assemblies_below_presentation_do_not_reference_desktop_UI_assemblies(string module)
    {
        using var stream = File.OpenRead(ModuleAssemblies.PathOf(module));
        using var pe = new PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var referenced = reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();

        referenced.Should().NotContain(r => DesktopUiAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase),
            $"{module} is below the presentation layer and must not compile against WPF/WinForms assemblies");
    }

    [Fact]
    public void Projects_below_presentation_have_no_desktop_framework_reference()
    {
        var offenders = RepositoryModel.SourceProjects
            .Where(p => !LayeringRules.L3.Contains(p.Name))
            .SelectMany(p => p.FrameworkReferences
                .Where(f => f.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{p.Name} -> {f}"))
            .ToList();

        offenders.Should().BeEmpty(
            $"projects below the presentation layer must not reference the desktop shared framework; a FrameworkReference to Microsoft.WindowsDesktop.App(.WPF/.WindowsForms) bypasses the UseWPF/UseWindowsForms check. Offending references: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void Shared_build_files_do_not_enable_desktop_UI_for_every_project()
    {
        var sharedFiles = new[]
            {
                "Directory.Build.props", "Directory.Build.targets",
                Path.Combine("src", "Directory.Build.props"), Path.Combine("src", "Directory.Build.targets"),
            }
            .Select(f => Path.Combine(RepositoryModel.Root, f))
            .Where(File.Exists);

        foreach (var file in sharedFiles)
        {
            var doc = XDocument.Load(file);
            doc.Descendants("FrameworkReference")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
                .Should().NotContain(f => f.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase),
                    $"{file} applies to every project, including those below the presentation layer");
            doc.Descendants("UseWPF").Concat(doc.Descendants("UseWindowsForms"))
                .Select(e => e.Value.Trim())
                .Should().NotContain(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase),
                    $"{file} applies to every project, including those below the presentation layer");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rule 2: no NexPane.Shell / platform interop types leaking into NexPane.Core contracts.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Core_contracts_do_not_depend_on_Shell_or_platform_interop_types()
    {
        var result = Types.FromFile(ModuleAssemblies.PathOf(LayeringRules.Core))
            .ShouldNot().HaveDependencyOnAny(InteropNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"NexPane.Core is the portable contracts layer; Windows interop belongs in NexPane.Shell behind Core abstractions. Offending types: {Describe(result)}");
    }

    [Fact]
    public void Core_declares_no_platform_invoke_methods()
    {
        var result = Types.FromFile(ModuleAssemblies.PathOf(LayeringRules.Core))
            .Should().MeetCustomRule(new NoPlatformInvokeRule())
            .GetResult();

        result.IsSuccessful.Should().BeTrue($"P/Invoke belongs in NexPane.Shell. Offending types: {Describe(result)}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rule 3: raw interop never part of a module's public surface.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ModulesBelowPresentation))]
    public void Platform_invoke_methods_are_never_externally_visible(string module)
    {
        var result = Types.FromFile(ModuleAssemblies.PathOf(module))
            .Should().MeetCustomRule(new NoVisiblePlatformInvokeRule())
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{module}: extern (DllImport/LibraryImport) methods must be private or internal and wrapped by a managed API. Offending types: {Describe(result)}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rule 4: implementations of Core contracts are internal (ARCHITECTURE §4: collaboration goes through interfaces
    // declared in NexPane.Core, wired in NexPane.App through each module's Add<Module>(IServiceCollection) extension).
    // Runs in the default suite since P3.39 made the Phase 3 implementations internal.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ModulesBelowPresentation))]
    public void Implementations_of_Core_contracts_are_internal(string module)
    {
        if (module == LayeringRules.Core)
        {
            return;
        }

        var result = Types.FromFile(ModuleAssemblies.PathOf(module))
            .That().AreClasses()
            .Should().MeetCustomRule(new CoreContractImplementationIsInternalRule())
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{module}: classes implementing NexPane.Core contracts must be internal and exposed through the Core interface plus Add<Module>. Offending types: {Describe(result)}");
    }

    private static string Describe(ArchResult result) =>
        result.FailingTypeNames is { Count: > 0 } names ? string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal)) : "(none)";
}

/// <summary>Locates the compiled module assemblies copied next to the tests.</summary>
internal static class ModuleAssemblies
{
    /// <summary>L0, L1, L2 and hosts (ARCHITECTURE §4): every module that must stay free of desktop UI.</summary>
    internal static IReadOnlyList<string> BelowPresentation { get; } =
        LayeringRules.L0.Concat(LayeringRules.L1).Concat(LayeringRules.L2).Concat(LayeringRules.Hosts).ToList();

    internal static string PathOf(string module) => Path.Combine(AppContext.BaseDirectory, module + ".dll");
}

/// <summary>Cecil helpers shared by the custom rules.</summary>
internal static class CecilVisibility
{
    /// <summary>True when the type is reachable from outside its assembly (public, and every declaring type public).</summary>
    internal static bool IsExternallyVisible(TypeDefinition type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            var visible = t.IsNested ? t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamilyOrAssembly : t.IsPublic;
            if (!visible)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsExternallyVisible(MethodDefinition method) =>
        (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) && IsExternallyVisible(method.DeclaringType);
}

/// <summary>Passes when the type declares no P/Invoke (extern) methods at all.</summary>
internal sealed class NoPlatformInvokeRule : ICustomRule
{
    public bool MeetsRule(TypeDefinition type) => !type.Methods.Any(m => m.IsPInvokeImpl);
}

/// <summary>Passes when none of the type's P/Invoke (extern) methods is visible outside the assembly.</summary>
internal sealed class NoVisiblePlatformInvokeRule : ICustomRule
{
    public bool MeetsRule(TypeDefinition type) =>
        !type.Methods.Any(m => m.IsPInvokeImpl && CecilVisibility.IsExternallyVisible(m));
}

/// <summary>
/// Passes unless the type is an externally visible, concrete class that implements an interface declared in NexPane.Core.
/// </summary>
internal sealed class CoreContractImplementationIsInternalRule : ICustomRule
{
    public bool MeetsRule(TypeDefinition type)
    {
        if (!type.IsClass || type.IsInterface || type.IsAbstract || !CecilVisibility.IsExternallyVisible(type))
        {
            return true;
        }

        return !type.Interfaces.Any(i => i.InterfaceType.FullName.StartsWith("NexPane.Core.", StringComparison.Ordinal));
    }
}
