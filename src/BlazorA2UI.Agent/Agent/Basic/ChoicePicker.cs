namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>ChoicePicker</c> component. Lets the user pick one or more options.
/// </summary>
public sealed class ChoicePicker : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "ChoicePicker";

    /// <summary>
    /// Gets or sets the label of the group (<c>label</c>).
    /// </summary>
    public A2UIValue? Label { get; init; }

    /// <summary>
    /// Gets or sets single or multiple selection; the renderer defaults to <see cref="ChoicePickerVariant.MutuallyExclusive"/> (<c>variant</c>).
    /// </summary>
    public ChoicePickerVariant? Variant { get; init; }

    /// <summary>
    /// Gets or sets the options (<c>options</c>).
    /// </summary>
    public required IReadOnlyList<ChoiceOption> Options { get; init; }

    /// <summary>
    /// Gets or sets the selected values; bind it to a string array in the data model (<c>value</c>).
    /// </summary>
    public required A2UIValue Value { get; init; }

    /// <summary>
    /// Gets or sets how options are drawn; the renderer defaults to <see cref="ChoiceDisplayStyle.Checkbox"/> (<c>displayStyle</c>).
    /// </summary>
    public ChoiceDisplayStyle? DisplayStyle { get; init; }

    /// <summary>
    /// Gets or sets whether a search box filters the options (<c>filterable</c>).
    /// </summary>
    public bool? Filterable { get; init; }

    /// <summary>
    /// Gets or sets validation rules (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("label", Label);
        writer.SetEnum("variant", Variant);
        JsonArray options = [];
        foreach (ChoiceOption option in Options)
        {
            options.Add(new JsonObject { ["label"] = option.Label.ToJson(), ["value"] = option.Value });
        }

        writer.SetJson("options", options);
        writer.Set("value", Value);
        writer.SetEnum("displayStyle", DisplayStyle);
        writer.Set("filterable", Filterable);
        writer.SetChecks(Checks);
    }
}
