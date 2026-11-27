using BlazorA2UI.Catalogs;
using BlazorA2UI.Client;
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

    /// <summary>
    /// Adds a scoped <see cref="A2UIAgentClient"/> that connects the scope's <see cref="MessageProcessor"/> to an agent
    /// endpoint. Call <see cref="AddA2UI"/> as well. Inject the client into a component, so it works on the renderer's
    /// synchronization context.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The options are missing an endpoint or are out of range.</exception>
    /// <param name="services">The app's services.</param>
    /// <param name="configure">Sets the endpoint and the other options.</param>
    /// <param name="httpClient">
    /// Returns the <see cref="HttpClient"/> to send with, which the client does not dispose; null uses the
    /// <see cref="HttpClient"/> registered in the services, as a WebAssembly app has.
    /// </param>
    /// <returns>The services, for chaining.</returns>
    public static IServiceCollection AddA2UIAgentClient(
        this IServiceCollection services,
        Action<A2UIAgentClientOptions> configure,
        Func<IServiceProvider, HttpClient>? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        A2UIAgentClientOptions options = new();
        configure(options);
        options.Validate();
        services.TryAddScoped(provider => new A2UIAgentClient(
            httpClient?.Invoke(provider)
                ?? provider.GetService<HttpClient>()
                ?? throw new InvalidOperationException("The A2UI agent client needs an HttpClient: register one, or pass a factory to AddA2UIAgentClient."),
            provider.GetRequiredService<MessageProcessor>(),
            options));
        return services;
    }
}
