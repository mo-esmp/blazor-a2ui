using BlazorA2UI.Catalogs;
using BlazorA2UI.Media;
using BlazorA2UI.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorA2UI;

/// <summary>
/// Registers the A2UI renderer with a Blazor app's services.
/// </summary>
public static class A2UIServiceCollectionExtensions
{
    /// <summary>
    /// Adds a scoped <see cref="MessageProcessor"/> with the basic catalog, the component registry, and the media
    /// allowlist. Media stay blocked until <paramref name="configure"/> allows a prefix or the host registers its
    /// own <see cref="IA2UIMediaResolver"/>.
    /// </summary>
    /// <param name="services">The app's services.</param>
    /// <param name="configure">Configures the renderer, or null for the defaults.</param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddA2UI(this IServiceCollection services, Action<A2UIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        A2UIOptions options = new();
        configure?.Invoke(options);

        services.Configure<A2UIMediaOptions>(media =>
        {
            foreach ((Uri prefix, A2UIMediaKind[] kinds) in options.Media.Prefixes)
            {
                media.Allow(prefix.AbsoluteUri, kinds);
            }
        });
        services.TryAddSingleton<IA2UIMediaResolver, AllowlistMediaResolver>();
        services.TryAddSingleton(options.Components);
        services.TryAddScoped(provider =>
        {
            // Each scope (a Blazor Server circuit, or the WebAssembly app) gets its own options, so function
            // implementations see that scope's services.
            A2UIProcessorOptions shared = options.Processor;
            A2UIProcessorOptions processorOptions = new()
            {
                AgentCallTimeout = shared.AgentCallTimeout,
                MaxPendingAgentCalls = shared.MaxPendingAgentCalls,
                MaxComponentsPerSurface = shared.MaxComponentsPerSurface,
                MaxTreeDepth = shared.MaxTreeDepth,
                SendErrorsToAgent = shared.SendErrorsToAgent,
                Validation = shared.Validation,
                Services = shared.Services ?? provider,
            };
            List<Catalog> catalogs = [BasicCatalog.Create(), .. options.Catalogs];
            return new MessageProcessor(catalogs, processorOptions);
        });
        return services;
    }
}
