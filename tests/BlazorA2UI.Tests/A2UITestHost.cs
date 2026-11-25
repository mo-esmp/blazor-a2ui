namespace BlazorA2UI.Tests;

/// <summary>
/// A bUnit context with the A2UI services registered, plus helpers that feed v1.0 messages to its processor.
/// </summary>
internal sealed class A2UITestHost : IDisposable
{
    /// <summary>
    /// The id of every surface the helpers create.
    /// </summary>
    internal const string SurfaceId = "s";

    /// <summary>
    /// Creates the host, letting <paramref name="configure"/> set the A2UI options (the media allowlist, for example).
    /// </summary>
    /// <param name="configure">Changes the options before the services are built, or null for the defaults.</param>
    internal A2UITestHost(Action<A2UIOptions>? configure = null)
    {
        Context = new BunitContext();
        Context.Services.AddA2UI(configure);
        Processor = Context.Services.GetRequiredService<MessageProcessor>();
        Processor.Outbound = message =>
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        };
    }

    /// <summary>
    /// The bUnit context that renders components.
    /// </summary>
    internal BunitContext Context { get; }

    /// <summary>
    /// The processor the surface component injects.
    /// </summary>
    internal MessageProcessor Processor { get; }

    /// <summary>
    /// The messages the processor sent to the agent, in order.
    /// </summary>
    internal List<A2UIOutboundMessage> Sent { get; } = [];

    /// <summary>
    /// Creates the surface and gives it <paramref name="components"/>, a JSON array of v1.0 components with a
    /// <c>root</c>. Throws when a message is rejected, so a test never passes on a surface that was not built.
    /// </summary>
    /// <param name="components">The components, as a JSON array.</param>
    /// <param name="data">The initial data model, as a JSON object, or null for none.</param>
    internal void Load(string components, string? data = null)
    {
        Processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"{{{SurfaceId}}}","catalogId":"{{{BasicCatalog.CatalogId}}}"}}"""));
        if (data is not null)
        {
            Processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","updateDataModel":{"surfaceId":"{{{SurfaceId}}}","path":"/","value":{{{data}}}}}"""));
        }

        Processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","updateComponents":{"surfaceId":"{{{SurfaceId}}}","components":{{{components}}}}}"""));
    }

    /// <summary>
    /// Renders an <see cref="A2UISurface"/> for the test surface.
    /// </summary>
    /// <param name="onAction">Receives the page-level action notifications, or null for none.</param>
    internal IRenderedComponent<A2UISurface> Render(Action<A2UIActionEventArgs>? onAction = null) =>
        Context.Render<A2UISurface>(parameters =>
        {
            parameters.Add(p => p.SurfaceId, SurfaceId);
            if (onAction is not null)
            {
                parameters.Add(p => p.OnAction, onAction);
            }
        });

    /// <summary>
    /// Disposes the bUnit context and the services it owns.
    /// </summary>
    public void Dispose() => Context.Dispose();
}
