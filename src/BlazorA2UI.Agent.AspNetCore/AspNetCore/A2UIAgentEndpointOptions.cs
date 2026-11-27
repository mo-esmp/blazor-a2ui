using System.Security.Claims;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;
using Microsoft.AspNetCore.Http;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Options for an endpoint mapped with
/// <see cref="A2UIAgentEndpointExtensions.MapA2UIAgent(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, A2UIAgentHandler, Action{A2UIAgentEndpointOptions})"/>.
/// </summary>
public sealed class A2UIAgentEndpointOptions
{
    /// <summary>
    /// The longest timeout a timer accepts.
    /// </summary>
    private static readonly TimeSpan s_maxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

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
    /// Gets or sets how a request names the user it is made for. A conversation belongs to the user who started it,
    /// and a request from anyone else gets 404, as if the id were unknown. Null means the conversation belongs to no
    /// one, and its id alone is enough to use it.
    /// </summary>
    /// <remarks>
    /// The default uses the authenticated user's <see cref="ClaimTypes.NameIdentifier"/> claim, then its <c>sub</c>
    /// claim, then its name, prefixed with the authentication type; an anonymous request names no one. Set it to
    /// include a tenant, or for a scheme whose users have none of those.
    /// </remarks>
    public Func<HttpContext, string?> ConversationOwner { get; set; } = DefaultConversationOwner;

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

        if (Catalogs.GroupBy(c => c.CatalogId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"The A2UI agent endpoint has two catalogs with the id '{duplicate.Key}'.");
        }

        if (MaxRequestBodySize <= 0 || MaxMessagesPerRequest <= 0 || MaxConversations <= 0 || MaxPendingRendererCalls <= 0)
        {
            throw new InvalidOperationException("The A2UI agent endpoint's limits must be positive.");
        }

        if (ConversationIdleTimeout <= TimeSpan.Zero || RendererFunctionTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The A2UI agent endpoint's timeouts must be positive.");
        }

        // Expiry checks run every half of the idle timeout, so both delays must fit a timer.
        if (RendererFunctionTimeout > s_maxTimerDelay || ConversationIdleTimeout / 2 > s_maxTimerDelay)
        {
            throw new InvalidOperationException($"The A2UI agent endpoint's renderer function timeout, and half its idle timeout, may be at most {s_maxTimerDelay}.");
        }

        if (ConversationOwner is null)
        {
            throw new InvalidOperationException("The A2UI agent endpoint needs a ConversationOwner function.");
        }

        if (TimeProvider is null)
        {
            throw new InvalidOperationException("The A2UI agent endpoint needs a TimeProvider.");
        }

        // Build what each conversation builds, so anything the parts refuse fails now and not on the first request.
        try
        {
            using A2UIConversation probe = new(new string('A', A2UIConversationStore.IdLength), null, this);
        }
        catch (Exception ex) when (ex is ArgumentException or A2UIException)
        {
            throw new InvalidOperationException($"The A2UI agent endpoint's options are not usable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Names the authenticated user of a request, or no one for an anonymous request.
    /// </summary>
    /// <returns>The user's key, or null.</returns>
    /// <param name="context">The request's context.</param>
    private static string? DefaultConversationOwner(HttpContext context)
    {
        ClaimsPrincipal user = context.User;
        if (user.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        string? key = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? identity.Name;

        // Users of a scheme that gives no key cannot be told apart, so such a scheme needs its own ConversationOwner;
        // they still cannot pick up anonymous conversations or those of users with a key.
        return $"{identity.AuthenticationType}:{key ?? string.Empty}";
    }
}
