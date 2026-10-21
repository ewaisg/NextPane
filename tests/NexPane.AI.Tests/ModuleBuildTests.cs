using NexPane.Tests.Shared;

namespace NexPane.AI.Tests;

public sealed class ModuleBuildTests
{
    [Fact]
    public void Module_is_built_x64_for_the_architecture_target_framework()
    {
        ModuleAssembly.AssertWindowsX64Module("NexPane.AI.dll");
    }
}
