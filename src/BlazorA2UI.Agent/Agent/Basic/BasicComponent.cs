namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// A component of the A2UI v1.0 basic catalog.
/// </summary>
public abstract class BasicComponent : A2UIComponent
{
    /// <summary>
    /// Creates a basic catalog component. Only this library defines them.
    /// </summary>
    private protected BasicComponent()
    {
    }

    /// <summary>
    /// Gets or sets the component's share of the free space in its parent (<c>weight</c>), like CSS <c>flex-grow</c>.
    /// Only a direct child of a <see cref="Row"/> or <see cref="Column"/> may set it.
    /// </summary>
    public double? Weight { get; init; }

    /// <inheritdoc />
    protected internal sealed override void WriteProperties(A2UIComponentWriter writer)
    {
        WriteBasicProperties(writer);
        writer.Set("weight", Weight);
    }

    /// <summary>
    /// Writes the properties this component type defines.
    /// </summary>
    /// <param name="writer">The writer for this component's properties.</param>
    private protected abstract void WriteBasicProperties(A2UIComponentWriter writer);
}

/// <summary>
/// One tab of <see cref="Tabs"/>.
/// </summary>
/// <param name="Title">The tab title.</param>
/// <param name="Child">The tab content.</param>
public sealed record Tab(A2UIValue Title, A2UIComponent Child);

/// <summary>
/// One option of a <see cref="ChoicePicker"/>.
/// </summary>
/// <param name="Label">The text shown for the option.</param>
/// <param name="Value">The stable value written to the data model when the option is selected.</param>
public sealed record ChoiceOption(A2UIValue Label, string Value);
