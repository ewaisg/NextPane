using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexPane.Core.Vfs;
using NexPane.Shell.FileSystem;
using NexPane.Shell.Launch;
using NexPane.Shell.Threading;

namespace NexPane.Shell;

/// <summary>Registers the NexPane.Shell services with the App's generic host (ARCHITECTURE §4 "Composition"):
/// implementations stay internal behind their Core / Shell interfaces.</summary>
public static class ShellServiceCollectionExtensions
{
    /// <summary>Configuration key for the Shell STA pool size (2–4; default from the processor count).</summary>
    public const string StaThreadCountKey = "Shell:StaThreadCount";

    /// <summary>Registers the local file system provider as <see cref="IFileSystemProvider"/> (its watchers are created by
    /// <see cref="IFileSystemProvider.CreateWatcher"/>), the Shell STA scheduler and the <see cref="IShellLauncher"/>.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<IFileSystemProvider, LocalFileSystemProvider>();

        var threadCount = int.TryParse(configuration[StaThreadCountKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : Environment.ProcessorCount / 4;
        threadCount = Math.Clamp(threadCount, ShellStaScheduler.MinThreads, ShellStaScheduler.MaxThreads);

        // TryAdd: calling AddShell twice must not create a second STA pool or launcher.
        services.TryAddSingleton(_ => new ShellStaScheduler(threadCount));
        services.TryAddSingleton<IShellLauncher>(provider => new ShellExecuteLauncher(provider.GetRequiredService<ShellStaScheduler>()));
        return services;
    }
}
