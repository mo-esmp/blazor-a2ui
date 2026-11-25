namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Icon</c> component. Displays an icon: a named icon from <see cref="IconNames"/>, or an SVG path.
/// </summary>
public sealed class Icon : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Icon";

    /// <summary>
    /// Gets or sets the icon name, such as <see cref="IconNames.Add"/>, or a binding to one. Set this or <see cref="SvgPath"/> (<c>name</c>).
    /// </summary>
    public A2UIValue? Name { get; init; }

    /// <summary>
    /// Gets or sets an SVG path to draw instead of a named icon. Set this or <see cref="Name"/> (<c>svgPath</c>).
    /// </summary>
    public A2UIValue? SvgPath { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        if ((Name is null) == (SvgPath is null))
        {
            throw new InvalidOperationException("An Icon needs exactly one of Name and SvgPath.");
        }

        writer.Set("name", Name);
        if (SvgPath is not null)
        {
            writer.SetJson("name", new JsonObject { ["svgPath"] = SvgPath.ToJson() });
        }
    }
}
