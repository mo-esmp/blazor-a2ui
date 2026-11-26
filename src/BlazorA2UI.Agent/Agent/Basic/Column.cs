namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Column</c> component. Lays out its children vertically.
/// </summary>
public sealed class Column : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Column";

    /// <summary>
    /// Gets or sets the children, or a template repeated over a data model list (<c>children</c>).
    /// </summary>
    public required A2UIChildList Children { get; init; }

    /// <summary>
    /// Gets or sets how children are spaced along the column; the renderer defaults to <see cref="Justify.Start"/> (<c>justify</c>).
    /// </summary>
    public Justify? Justify { get; init; }

    /// <summary>
    /// Gets or sets how children are aligned across the column; the renderer defaults to <see cref="Alignment.Stretch"/> (<c>align</c>).
    /// </summary>
    public Alignment? Align { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetChildren("children", Children);
        writer.SetEnum("justify", Justify);
        writer.SetEnum("align", Align);
    }
}
