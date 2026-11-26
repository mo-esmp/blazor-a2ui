namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>List</c> component. A scrollable list of children.
/// </summary>
public sealed class List : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "List";

    /// <summary>
    /// Gets or sets the items, or a template repeated over a data model list (<c>children</c>).
    /// </summary>
    public required A2UIChildList Children { get; init; }

    /// <summary>
    /// Gets or sets the scroll direction; the renderer defaults to <see cref="ListDirection.Vertical"/> (<c>direction</c>).
    /// </summary>
    public ListDirection? Direction { get; init; }

    /// <summary>
    /// Gets or sets how items are aligned across the list; the renderer defaults to <see cref="Alignment.Stretch"/> (<c>align</c>).
    /// </summary>
    public Alignment? Align { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetChildren("children", Children);
        writer.SetEnum("direction", Direction);
        writer.SetEnum("align", Align);
    }
}
