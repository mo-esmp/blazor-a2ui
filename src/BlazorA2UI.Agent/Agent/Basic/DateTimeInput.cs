namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>DateTimeInput</c> component. A date and time input bound to the data model, as ISO 8601 text.
/// </summary>
public sealed class DateTimeInput : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "DateTimeInput";

    /// <summary>
    /// Gets or sets the value; bind it to a data model path so the input writes back (<c>value</c>).
    /// </summary>
    public required A2UIValue Value { get; init; }

    /// <summary>
    /// Gets or sets whether the user picks a date (<c>enableDate</c>).
    /// </summary>
    public bool? EnableDate { get; init; }

    /// <summary>
    /// Gets or sets whether the user picks a time (<c>enableTime</c>).
    /// </summary>
    public bool? EnableTime { get; init; }

    /// <summary>
    /// Gets or sets the earliest allowed value, in ISO 8601 (<c>min</c>).
    /// </summary>
    public A2UIValue? Min { get; init; }

    /// <summary>
    /// Gets or sets the latest allowed value, in ISO 8601 (<c>max</c>).
    /// </summary>
    public A2UIValue? Max { get; init; }

    /// <summary>
    /// Gets or sets the label (<c>label</c>).
    /// </summary>
    public A2UIValue? Label { get; init; }

    /// <summary>
    /// Gets or sets validation rules (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("value", Value);
        writer.Set("enableDate", EnableDate);
        writer.Set("enableTime", EnableTime);
        writer.Set("min", Min);
        writer.Set("max", Max);
        writer.Set("label", Label);
        writer.SetChecks(Checks);
    }
}
