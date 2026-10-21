using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NexPane.IndexService.Tests;

public sealed class HostLifetimeTests
{
    [Fact]
    public async Task Host_starts_and_stops_cleanly_within_timeout()
    {
        using var host = Program.BuildHost([]);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var started = false;
        var stopped = false;
        lifetime.ApplicationStarted.Register(() => started = true);
        lifetime.ApplicationStopped.Register(() => stopped = true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StartAsync(timeout.Token);
        started.Should().BeTrue();

        await host.StopAsync(timeout.Token);
        stopped.Should().BeTrue();
    }

    [Fact]
    public void Service_name_matches_installer_registration()
    {
        Program.ServiceName.Should().Be("NexPaneIndex");
    }
}
