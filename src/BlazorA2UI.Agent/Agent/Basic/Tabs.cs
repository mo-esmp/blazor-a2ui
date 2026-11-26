namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Tabs</c> component. A set of tabs, each with a title and content.
/// </summary>
public sealed class Tabs : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Tabs";

    /// <summary>
    /// Gets or sets the tabs, at least one (<c>tabs</c>).
    /// </summary>
    public required IReadOnlyList<Tab> Items { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        if (Items.Count == 0)
        {
            throw new InvalidOperationException("Tabs needs at least one tab.");
        }

        JsonArray tabs = [];
        foreach (Tab tab in Items)
        {
            tabs.Add(new JsonObject { ["title"] = tab.Title.ToJson(), ["child"] = writer.ChildId(tab.Child) });
        }

        writer.SetJson("tabs", tabs);
    }
}
