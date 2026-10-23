using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexPane.Core.Vfs;
using NexPane.Shell.FileSystem;

namespace NexPane.Shell;

/// <summary>Composition of the Shell module (§4): implementations stay internal behind their Core interfaces.</summary>
public static class ShellServiceCollectionExtensions
{
    /// <summary>Registers the local file system provider as <see cref="IFileSystemProvider"/> (its watchers are created by
    /// <see cref="IFileSystemProvider.CreateWatcher"/>).</summary>
    public static IServiceCollection AddShell(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<IFileSystemProvider, LocalFileSystemProvider>();
        return services;
    }
}
