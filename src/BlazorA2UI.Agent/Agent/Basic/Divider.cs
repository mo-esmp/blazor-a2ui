namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Divider</c> component. A dividing line.
/// </summary>
public sealed class Divider : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Divider";

    /// <summary>
    /// Gets or sets the line's direction; the renderer defaults to <see cref="DividerAxis.Horizontal"/> (<c>axis</c>).
    /// </summary>
    public DividerAxis? Axis { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetEnum("axis", Axis);
    }
}
