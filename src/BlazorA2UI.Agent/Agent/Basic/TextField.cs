namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>TextField</c> component. A text input bound to the data model.
/// </summary>
public sealed class TextField : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "TextField";

    /// <summary>
    /// Gets or sets the label (<c>label</c>).
    /// </summary>
    public required A2UIValue Label { get; init; }

    /// <summary>
    /// Gets or sets the value; bind it to a data model path so the input writes back (<c>value</c>).
    /// </summary>
    public required A2UIValue Value { get; init; }

    /// <summary>
    /// Gets or sets the hint shown while the input is empty (<c>placeholder</c>).
    /// </summary>
    public A2UIValue? Placeholder { get; init; }

    /// <summary>
    /// Gets or sets the input type; the renderer defaults to <see cref="TextFieldVariant.ShortText"/> (<c>variant</c>).
    /// </summary>
    public TextFieldVariant? Variant { get; init; }

    /// <summary>
    /// Gets or sets validation rules (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("label", Label);
        writer.Set("value", Value);
        writer.Set("placeholder", Placeholder);
        writer.SetEnum("variant", Variant);
        writer.SetChecks(Checks);
    }
}
