namespace BlazorA2UI.Agent;

/// <summary>
/// Accessibility attributes for a component (<c>@accessibility</c>).
/// </summary>
public sealed class A2UIAccessibility
{
    /// <summary>
    /// Gets or sets a short label, typically one to three words, that says what the element is for.
    /// </summary>
    public A2UIValue? Label { get; init; }

    /// <summary>
    /// Gets or sets more information about the element, such as instructions or format requirements.
    /// </summary>
    public A2UIValue? Description { get; init; }

    /// <summary>
    /// Gets or sets how screen readers announce updates to the element.
    /// </summary>
    public A2UILive? Live { get; init; }

    /// <summary>
    /// Gets or sets whether the element and its children are hidden from assistive technologies.
    /// </summary>
    public A2UIValue? Hidden { get; init; }

    /// <summary>
    /// Returns the attributes' wire form.
    /// </summary>
    public JsonObject ToJson()
    {
        JsonObject json = new();
        if (Label is not null)
        {
            json["label"] = Label.ToJson();
        }

        if (Description is not null)
        {
            json["description"] = Description.ToJson();
        }

        if (Live is { } live)
        {
            json["live"] = A2UIEnum.ToWire(live);
        }

        if (Hidden is not null)
        {
            json["hidden"] = Hidden.ToJson();
        }

        return json;
    }
}

/// <summary>
/// How screen readers announce updates (WAI-ARIA <c>aria-live</c>).
/// </summary>
public enum A2UILive
{
    /// <summary>
    /// Updates are not announced.
    /// </summary>
    Off,

    /// <summary>
    /// Updates are announced when the user pauses.
    /// </summary>
    Polite,

    /// <summary>
    /// Updates interrupt the user immediately.
    /// </summary>
    Assertive,
}
