namespace BlazorA2UI.Agent;

/// <summary>
/// Builds agent-to-renderer messages. Each method returns a complete envelope with <c>"version": "1.0"</c>, ready for
/// <see cref="A2UIMessageWriter"/> or any other transport.
/// </summary>
public static class A2UIMessages
{
    /// <summary>
    /// The id the root component of every surface has.
    /// </summary>
    public const string RootId = "root";

    /// <summary>
    /// Builds a <c>createSurface</c> message. With a <paramref name="root"/>, the surface's whole component tree goes in
    /// the same message; the root gets the id <c>root</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="surfaceId"/> is empty, or the root has an id other than <c>root</c>.</exception>
    /// <exception cref="InvalidOperationException">The tree repeats an id, contains itself, or is too deep or too large.</exception>
    /// <param name="surfaceId">The surface id, unique for the renderer's lifetime.</param>
    /// <param name="root">The component tree, or null to send components later with <see cref="UpdateComponents(string, A2UIComponent)"/>.</param>
    /// <param name="dataModel">The initial data model, or null. It is copied.</param>
    /// <param name="catalogId">The surface's default catalog. Defaults to the basic catalog; null leaves it out.</param>
    /// <param name="sendDataModel">Whether the renderer sends the surface's data model with every message to the agent.</param>
    /// <param name="metadata">Surface metadata, such as <c>{"extensions": {...}}</c>, or null. It is copied.</param>
    public static JsonObject CreateSurface(
        string surfaceId,
        A2UIComponent? root = null,
        JsonObject? dataModel = null,
        string? catalogId = A2UIProtocol.BasicCatalogId,
        bool sendDataModel = false,
        JsonObject? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        JsonObject body = new() { ["surfaceId"] = surfaceId };
        if (catalogId is not null)
        {
            body["catalogId"] = catalogId;
        }

        if (sendDataModel)
        {
            body["sendDataModel"] = true;
        }

        if (dataModel is not null)
        {
            body["dataModel"] = dataModel.DeepClone();
        }

        if (root is not null)
        {
            body["components"] = ComponentFlattener.Flatten([root], RootId);
        }

        if (metadata is not null)
        {
            body["metadata"] = metadata.DeepClone();
        }

        return Envelope("createSurface", body);
    }

    /// <summary>
    /// Builds an <c>updateComponents</c> message that sends a surface's whole tree. The root gets the id <c>root</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="surfaceId"/> is empty, or the root has an id other than <c>root</c>.</exception>
    /// <exception cref="InvalidOperationException">The tree repeats an id, contains itself, or is too deep or too large.</exception>
    /// <param name="surfaceId">The surface id.</param>
    /// <param name="root">The root of the tree.</param>
    public static JsonObject UpdateComponents(string surfaceId, A2UIComponent root)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        ArgumentNullException.ThrowIfNull(root);
        return Envelope("updateComponents", new JsonObject
        {
            ["surfaceId"] = surfaceId,
            ["components"] = ComponentFlattener.Flatten([root], RootId),
        });
    }

    /// <summary>
    /// Builds an <c>updateComponents</c> message that adds or replaces parts of a surface. Each component in
    /// <paramref name="components"/> needs an id: a component with an id already on the surface is replaced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="surfaceId"/> is empty, <paramref name="components"/> is empty, or a top-level component has no id.
    /// </exception>
    /// <exception cref="InvalidOperationException">The trees repeat an id, contain themselves, or are too deep or too large.</exception>
    /// <param name="surfaceId">The surface id.</param>
    /// <param name="components">The components to add or replace, each with its subtree.</param>
    public static JsonObject UpdateComponents(string surfaceId, IEnumerable<A2UIComponent> components)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        ArgumentNullException.ThrowIfNull(components);
        List<A2UIComponent> tops = [.. components];
        if (tops.Count == 0)
        {
            throw new ArgumentException("An update needs at least one component.", nameof(components));
        }

        return Envelope("updateComponents", new JsonObject
        {
            ["surfaceId"] = surfaceId,
            ["components"] = ComponentFlattener.Flatten(tops, null),
        });
    }

    /// <summary>
    /// Builds an <c>updateDataModel</c> message that sets the value at a path. A null value deletes the key at the path.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="surfaceId"/> is empty.</exception>
    /// <param name="surfaceId">The surface id.</param>
    /// <param name="path">The JSON Pointer path, or null for the whole data model.</param>
    /// <param name="value">The new value, copied; null deletes the key.</param>
    public static JsonObject UpdateDataModel(string surfaceId, string? path, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        JsonObject body = new() { ["surfaceId"] = surfaceId };
        if (path is not null)
        {
            body["path"] = path;
        }

        body["value"] = value?.DeepClone();
        return Envelope("updateDataModel", body);
    }

    /// <summary>
    /// Builds a <c>deleteSurface</c> message.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="surfaceId"/> is empty.</exception>
    /// <param name="surfaceId">The surface id.</param>
    public static JsonObject DeleteSurface(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        return Envelope("deleteSurface", new JsonObject { ["surfaceId"] = surfaceId });
    }

    /// <summary>
    /// Wraps a message body in an envelope.
    /// </summary>
    /// <param name="kind">The message kind.</param>
    /// <param name="body">The body.</param>
    private static JsonObject Envelope(string kind, JsonObject body) => new()
    {
        ["version"] = A2UIProtocol.Version,
        [kind] = body,
    };
}
