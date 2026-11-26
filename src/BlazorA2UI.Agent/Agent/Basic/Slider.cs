namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Slider</c> component. A slider that picks a number.
/// </summary>
public sealed class Slider : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Slider";

    /// <summary>
    /// Gets or sets the label (<c>label</c>).
    /// </summary>
    public A2UIValue? Label { get; init; }

    /// <summary>
    /// Gets or sets the minimum; the renderer defaults to 0 (<c>min</c>).
    /// </summary>
    public double? Min { get; init; }

    /// <summary>
    /// Gets or sets the maximum (<c>max</c>).
    /// </summary>
    public required double Max { get; init; }

    /// <summary>
    /// Gets or sets the value; bind it to a data model path so the slider writes back (<c>value</c>).
    /// </summary>
    public required A2UIValue Value { get; init; }

    /// <summary>
    /// Gets or sets the number of steps between the minimum and the maximum (<c>steps</c>).
    /// </summary>
    public int? Steps { get; init; }

    /// <summary>
    /// Gets or sets validation rules (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("label", Label);
        writer.Set("min", Min);
        writer.Set("max", Max);
        writer.Set("value", Value);
        writer.Set("steps", (double?)Steps);
        writer.SetChecks(Checks);
    }
}
