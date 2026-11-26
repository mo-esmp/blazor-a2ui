using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Maps an A2UI agent endpoint.
/// </summary>
public static class A2UIAgentEndpointExtensions
{
    /// <summary>
    /// The header that carries the conversation's id: the response sets it, and the client sends it back with each
    /// later request of the conversation. A browser client on another origin needs it in the CORS policy's exposed
    /// headers.
    /// </summary>
    public const string ConversationHeader = "A2UI-Conversation";

    /// <summary>
    /// Maps a <c>POST</c> endpoint that serves an A2UI agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client posts <c>{"text": "...", "messages": [...], "metadata": {...}}</c> as <c>application/json</c>: the
    /// user's text, the renderer's messages (<c>action</c>, <c>callAgentFunction</c>, <c>rendererFunctionResponse</c>,
    /// <c>error</c>) and optional metadata. The response streams the agent's messages as JSON Lines, or as server-sent
    /// events (<c>a2ui</c>, <c>text</c> and <c>error</c> events) when the client accepts <c>text/event-stream</c>. The
    /// turn ends when the response ends.
    /// </para>
    /// <para>
    /// The first response carries a new conversation id in <see cref="ConversationHeader"/>. The server keeps the
    /// conversation's surfaces, to check the agent's messages against them, and its pending renderer function calls.
    /// The id is a random 128-bit value; protect the endpoint with authentication as you would any API, for example
    /// with <c>RequireAuthorization</c> on the returned builder.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The options have no catalog or a limit out of range.</exception>
    /// <returns>A builder to add authorization, CORS or other conventions to the endpoint.</returns>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="handler">Runs each agent turn.</param>
    /// <param name="configure">Sets the endpoint's options; at least one catalog is required.</param>
    public static IEndpointConventionBuilder MapA2UIAgent(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        A2UIAgentHandler handler,
        Action<A2UIAgentEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(configure);

        A2UIAgentEndpointOptions options = new();
        configure(options);
        options.Validate();

        ILoggerFactory loggers = endpoints.ServiceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        A2UIConversationStore store = new(options);
        endpoints.ServiceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(store.Dispose);
        A2UIAgentEndpoint endpoint = new(options, store, handler, loggers.CreateLogger("BlazorA2UI.Agent.AspNetCore.A2UIAgentEndpoint"));
        return endpoints.MapPost(pattern, endpoint.HandleAsync).WithDisplayName($"A2UI agent {pattern}");
    }
}
