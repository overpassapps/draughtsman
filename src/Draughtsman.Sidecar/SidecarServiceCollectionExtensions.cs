using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Sidecar;

public static class SidecarServiceCollectionExtensions
{
    /// <summary>Registers one <see cref="ISidecarClient"/> for the process and the hosted service that starts and stops it.</summary>
    public static IServiceCollection AddSidecar(this IServiceCollection services, SidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<NodeSidecarClient>();
        services.AddSingleton<ISidecarClient>(provider => provider.GetRequiredService<NodeSidecarClient>());
        services.AddHostedService<SidecarHostedService>();
        return services;
    }
}
