using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NexPane.IndexService;

/// <summary>
/// Entry point of the NexPane Index Service (Windows service name <c>NexPaneIndex</c>).
/// Runs as a Windows service when started by the SCM, or as a console host otherwise (Ctrl+C stops it).
/// Hosts the USN/MFT named-pipe server (<see cref="IndexPipeServer"/>, task P3.13).
/// </summary>
internal static class Program
{
    internal const string ServiceName = "NexPaneIndex";

    private static async Task<int> Main(string[] args)
    {
        using var host = BuildHost(args);
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    internal static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        return builder.Build();
    }
}
