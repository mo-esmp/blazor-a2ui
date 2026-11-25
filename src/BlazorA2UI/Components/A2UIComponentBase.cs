using BlazorA2UI.Rendering;
using Microsoft.AspNetCore.Components;

namespace BlazorA2UI.Components;

/// <summary>
/// Base class for the Razor components that render A2UI components. It reads properties through the surface's
/// evaluator, so bindings and function calls resolve, and it exposes the safe building blocks the rendering
/// rules allow: text, enumerated CSS classes, children, actions and two-way writes.
/// </summary>
public abstract class A2UIComponentBase : ComponentBase
{
    /// <summary>
    /// Gets or sets the surface the component belongs to.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public Surface Surface { get; set; } = null!; // Blazor sets every [EditorRequired] parameter before the first render.

    /// <summary>
    /// Gets or sets the component to render.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public ComponentInstance Component { get; set; } = null!; // Set by Blazor before the first render, like Surface.

    /// <summary>
    /// Gets or sets the data scope the component's bindings resolve against.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public EvaluationContext Context { get; set; } = null!; // Set by Blazor before the first render, like Surface.

    /// <summary>
    /// Gets or sets the surface host, which forwards finished actions to the page.
    /// </summary>
    [CascadingParameter]
    internal A2UISurface? Host { get; set; }

    /// <summary>
    /// Gets or sets how deep the component is in the rendered tree, to stop runaway nesting.
    /// </summary>
    [Parameter]
    public int Depth { get; set; }

    /// <summary>
    /// Evaluates a property. Errors are reported once and give null, and so does a value still waiting on an agent call.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected JsonNode? Property(string name) => Surface.Evaluate(Component.Properties[name], Context, out _);

    /// <summary>
    /// Evaluates a property as display text; null and errors give an empty string.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected string Text(string name) => A2UIValues.ToDisplayString(Property(name));

    /// <summary>
    /// Evaluates a property as text, or null when it is missing or not a string.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected string? OptionalText(string name) =>
        Component.Properties.ContainsKey(name) ? A2UIValues.AsString(Property(name)) : null;

    /// <summary>
    /// Evaluates a property as a boolean; a missing value gives <paramref name="fallback"/>.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="fallback">The value when the property is missing.</param>
    protected bool Boolean(string name, bool fallback = false) =>
        Component.Properties.ContainsKey(name) ? A2UIValues.ToBoolean(Property(name)) : fallback;

    /// <summary>
    /// Evaluates a property as a finite number, or null when it is missing or not a finite number.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected double? Number(string name) =>
        A2UIValues.ToDouble(Property(name)) is { } n && double.IsFinite(n) ? n : null;

    /// <summary>
    /// The component's raw, unevaluated property, for enumerations that map to classes.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected JsonNode? Raw(string name) => Component.Properties[name];

    /// <summary>
    /// The weight class for the component, or null.
    /// </summary>
    protected string? WeightClass => A2UIClasses.Weight(Component.Properties["weight"]);

    /// <summary>
    /// The children a container property resolves to, each with its data scope.
    /// </summary>
    /// <param name="name">The property holding a child id, a list of ids or a template.</param>
    protected IReadOnlyList<(ComponentInstance Component, EvaluationContext Context)> Children(string name) =>
        Surface.ResolveChildren(Component, name, Context);

    /// <summary>
    /// The children of a container property with a stable render key each, so Blazor keeps a child's state with
    /// the child when the agent reorders siblings. The key is the child's id and data scope (template items share an
    /// id); a repeated id in a malformed list gets its position added, since duplicate keys would fail the render.
    /// </summary>
    /// <param name="name">The property holding a list of ids or a template.</param>
    protected List<(ComponentInstance Component, EvaluationContext Context, string Key)> KeyedChildren(string name)
    {
        IReadOnlyList<(ComponentInstance Component, EvaluationContext Context)> children = Children(name);
        List<(ComponentInstance, EvaluationContext, string)> keyed = new(children.Count);
        HashSet<string> used = new(StringComparer.Ordinal);
        for (int i = 0; i < children.Count; i++)
        {
            (ComponentInstance child, EvaluationContext context) = children[i];
            string key = string.Concat(child.Id, "@", context.Path);
            if (!used.Add(key))
            {
                key = string.Concat(key, "#", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                used.Add(key);
            }

            keyed.Add((child, context, key));
        }

        return keyed;
    }

    /// <summary>
    /// The component's <c>@checks</c> result in the current data.
    /// </summary>
    protected CheckOutcome Checks() => Surface.EvaluateChecks(Component, Context);

    /// <summary>
    /// Writes a user's input through a two-way bound property. A property that is not a <c>{"@path"}</c> binding is not written.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The new value.</param>
    protected void Write(string name, JsonNode? value) => Surface.Write(Component.Properties[name], Context, value);

    /// <summary>
    /// The ARIA attributes from the component's <c>@accessibility</c>. Only fixed attribute names are produced, and
    /// <c>live</c> only from its enumeration.
    /// </summary>
    protected IReadOnlyDictionary<string, object>? AccessibilityAttributes()
    {
        if (Component.Accessibility is not { } accessibility)
        {
            return null;
        }

        Dictionary<string, object> attributes = new(StringComparer.Ordinal);
        if (A2UIValues.AsString(Surface.Evaluate(accessibility["label"], Context, out _)) is { Length: > 0 } label)
        {
            attributes["aria-label"] = label;
        }

        if (A2UIValues.AsString(Surface.Evaluate(accessibility["description"], Context, out _)) is { Length: > 0 } description)
        {
            attributes["aria-description"] = description;
        }

        if (A2UIValues.AsString(accessibility["live"]) is "off" or "polite" or "assertive")
        {
            attributes["aria-live"] = A2UIValues.AsString(accessibility["live"]) ?? "off";
        }

        if (accessibility.ContainsKey("hidden") && A2UIValues.ToBoolean(Surface.Evaluate(accessibility["hidden"], Context, out _)))
        {
            attributes["aria-hidden"] = "true";
        }

        return attributes.Count == 0 ? null : attributes;
    }

    /// <summary>
    /// Runs the component's <c>action</c> after a user gesture, then tells the page through the surface host.
    /// Failures are reported by the processor and stop the action; the page is told only about an action that ran.
    /// </summary>
    protected async Task RunActionAsync()
    {
        // Captured before the await: a streamed update can replace this component's parameters while an agent
        // call in the action is pending, and the notification must name the source the user acted on.
        Surface surface = Surface;
        ComponentInstance component = Component;
        EvaluationContext context = Context;
        A2UISurface? host = Host;
        if (await surface.DispatchActionAsync(component, context) && host is not null)
        {
            await host.NotifyActionAsync(new A2UIActionEventArgs(surface.SurfaceId, component.Id, context));
        }
    }
}
