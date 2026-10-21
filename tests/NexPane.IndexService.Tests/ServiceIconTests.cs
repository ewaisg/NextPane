using System.Runtime.InteropServices;

namespace NexPane.IndexService.Tests;

/// <summary>task P2.07: NexPane.IndexService.exe carries the app icon (ApplicationIcon).</summary>
public sealed partial class ServiceIconTests
{
    [Fact]
    public void ExtractIconEx_finds_an_icon_in_the_built_service_exe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "NexPane.IndexService.exe");
        File.Exists(exe).Should().BeTrue();
        ExtractIconExW(exe, -1, 0, 0, 0).Should().BeGreaterThanOrEqualTo(1);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint large, nint small, uint icons);
}
