using NexPane.Tests.Shared;

namespace NexPane.Archives.Tests;

public sealed class ModuleBuildTests
{
    [Fact]
    public void Module_is_built_x64_for_the_architecture_target_framework()
    {
        ModuleAssembly.AssertWindowsX64Module("NexPane.Archives.dll");
    }
}
