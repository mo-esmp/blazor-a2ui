using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Options for an endpoint mapped with
/// <see cref="A2UIAgentEndpointExtensions.MapA2UIAgent(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, A2UIAgentHandler, Action{A2UIAgentEndpointOptions})"/>.
/// </summary>
public sealed class A2UIAgentEndpointOptions
{
    /// <summary>
    /// Gets the catalogs the agent's surfaces use. Messages the agent sends are checked against them, and agent and
    /// renderer function calls are checked against their declarations. At least one is required.
    /// </summary>
    public IList<Catalog> Catalogs { get; } = [];

    /// <summary>
    /// Gets or sets the functions the agent implements, which answer the renderer's <c>callAgentFunction</c> messages.
    /// Null means the agent implements none, and every call is answered with <c>UNKNOWN_FUNCTION</c>.
    /// </summary>
    public A2UIAgentFunctions? AgentFunctions { get; set; }

    /// <summary>
    /// Gets or sets the rules for checking the agent's messages, or null for the renderer's defaults.
    /// </summary>
    public A2UIValidationConfig? Validation { get; set; }

    /// <summary>
    /// Gets or sets the largest request body accepted, in bytes. The default is 1 MiB.
    /// </summary>
    public int MaxRequestBodySize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the most renderer messages one request may carry. The default is 100.
    /// </summary>
    public int MaxMessagesPerRequest { get; set; } = 100;

    /// <summary>
    /// Gets or sets the most conversations kept at once. A request that would start one more is refused with 503 until
    /// one expires. The default is 10,000.
    /// </summary>
    public int MaxConversations { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets how long a conversation with no request in progress is kept. The default is 30 minutes.
    /// </summary>
    public TimeSpan ConversationIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets how long a renderer function call waits for its response. The default is 30 seconds.
    /// </summary>
    public TimeSpan RendererFunctionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the most renderer function calls one conversation may have waiting at once. The default is 16.
    /// </summary>
    public int MaxPendingRendererCalls { get; set; } = 16;

    /// <summary>
    /// Gets or sets the clock for timeouts and expiry. The default is the system clock.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Checks that the options can serve requests.
    /// </summary>
    /// <exception cref="InvalidOperationException">An option is missing or out of range.</exception>
    internal void Validate()
    {
        if (Catalogs.Count == 0)
        {
            throw new InvalidOperationException("The A2UI agent endpoint needs at least one catalog.");
        }

        if (Catalogs.Any(c => c is null))
        {
            throw new InvalidOperationException("The A2UI agent endpoint's catalogs may not contain null.");
        }

        if (MaxRequestBodySize <= 0 || MaxMessagesPerRequest <= 0 || MaxConversations <= 0 || MaxPendingRendererCalls <= 0)
        {
            throw new InvalidOperationException("The A2UI agent endpoint's limits must be positive.");
        }

        if (ConversationIdleTimeout <= TimeSpan.Zero || RendererFunctionTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The A2UI agent endpoint's timeouts must be positive.");
        }

        if (TimeProvider is null)
        {
            throw new InvalidOperationException("The A2UI agent endpoint needs a TimeProvider.");
        }
    }
}
