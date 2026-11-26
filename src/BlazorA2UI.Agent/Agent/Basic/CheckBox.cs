namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>CheckBox</c> component. A check box bound to the data model.
/// </summary>
public sealed class CheckBox : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "CheckBox";

    /// <summary>
    /// Gets or sets the label (<c>label</c>).
    /// </summary>
    public required A2UIValue Label { get; init; }

    /// <summary>
    /// Gets or sets whether it is checked; bind it to a data model path so the box writes back (<c>value</c>).
    /// </summary>
    public required A2UIValue Value { get; init; }

    /// <summary>
    /// Gets or sets validation rules (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("label", Label);
        writer.Set("value", Value);
        writer.SetChecks(Checks);
    }
}
