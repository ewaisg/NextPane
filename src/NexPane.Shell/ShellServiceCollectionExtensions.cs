using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexPane.Shell.Launch;
using NexPane.Shell.Threading;

namespace NexPane.Shell;

/// <summary>Registers the NexPane.Shell services with the App's generic host (ARCHITECTURE §4 "Composition").</summary>
public static class ShellServiceCollectionExtensions
{
    /// <summary>Configuration key for the Shell STA pool size (2–4; default from the processor count).</summary>
    public const string StaThreadCountKey = "Shell:StaThreadCount";

    public static IServiceCollection AddShell(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var threadCount = int.TryParse(configuration[StaThreadCountKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : Environment.ProcessorCount / 4;
        threadCount = Math.Clamp(threadCount, ShellStaScheduler.MinThreads, ShellStaScheduler.MaxThreads);

        services.AddSingleton(_ => new ShellStaScheduler(threadCount));
        services.AddSingleton<IShellLauncher>(provider => new ShellExecuteLauncher(provider.GetRequiredService<ShellStaScheduler>()));
        return services;
    }
}
