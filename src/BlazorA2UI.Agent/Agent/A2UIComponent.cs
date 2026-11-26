namespace BlazorA2UI.Agent;

/// <summary>
/// A component in a tree that an agent builds. Containers hold their children as objects, and
/// <see cref="A2UIMessages"/> flattens the tree into the wire format: a flat list where parents reference children by id.
/// Derive from this class to build components of a custom catalog.
/// </summary>
public abstract class A2UIComponent
{
    /// <summary>
    /// Gets or sets the component id. Leave it unset to let the builder assign one. Set it on a component that a later
    /// message updates, so the update can name it.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gets or sets the catalog that defines this component, when it is not the surface's default catalog (<c>@catalogId</c>).
    /// </summary>
    public string? CatalogId { get; init; }

    /// <summary>
    /// Gets or sets the accessibility attributes (<c>@accessibility</c>).
    /// </summary>
    public A2UIAccessibility? Accessibility { get; init; }

    /// <summary>
    /// Gets or sets component-level metadata for vendor extensions (<c>@metadata</c>), such as <c>{"extensions": {...}}</c>.
    /// </summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>
    /// Gets the component type name in its catalog, such as <c>Text</c>.
    /// </summary>
    public abstract string ComponentName { get; }

    /// <summary>
    /// Returns a reference to a component that already exists on the surface, such as one sent in an earlier message.
    /// It places that component as a child without sending it again.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
    /// <param name="id">The existing component's id.</param>
    public static A2UIComponent Ref(string id) => new A2UIComponentRef(id);

    /// <summary>
    /// Writes the component's own properties. The builder writes <c>id</c>, <c>component</c>, <c>@catalogId</c>,
    /// <c>@accessibility</c> and <c>@metadata</c>.
    /// </summary>
    /// <param name="writer">The writer for this component's properties.</param>
    protected internal abstract void WriteProperties(A2UIComponentWriter writer);
}

/// <summary>
/// A reference to a component that is already on the surface. It is never sent, only referenced.
/// </summary>
internal sealed class A2UIComponentRef : A2UIComponent
{
    /// <summary>
    /// Creates a reference.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
    /// <param name="id">The existing component's id.</param>
    public A2UIComponentRef(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
    }

    /// <inheritdoc />
    public override string ComponentName => string.Empty;

    /// <inheritdoc />
    protected internal override void WriteProperties(A2UIComponentWriter writer)
    {
        // A reference has no properties of its own: the builder never writes it.
    }
}
