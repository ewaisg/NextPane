using NexPane.Tests.Shared;

namespace NexPane.UITests;

public sealed class AppBuildTests
{
    [Fact]
    public void Application_is_built_x64_for_the_architecture_target_framework()
    {
        ModuleAssembly.AssertWindowsX64Module("NexPane.dll");
    }

    [Fact]
    public void Application_executable_is_present_next_to_the_tests()
    {
        File.Exists(Path.Combine(AppContext.BaseDirectory, "NexPane.exe")).Should().BeTrue();
    }
}
