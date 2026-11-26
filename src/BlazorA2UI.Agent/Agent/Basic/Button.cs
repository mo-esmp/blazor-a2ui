namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Button</c> component. A button that runs an action.
/// </summary>
public sealed class Button : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Button";

    /// <summary>
    /// Gets or sets the content, usually a <see cref="Text"/> (<c>child</c>).
    /// </summary>
    public required A2UIComponent Child { get; init; }

    /// <summary>
    /// Gets or sets what the button does (<c>action</c>).
    /// </summary>
    public required A2UIAction Action { get; init; }

    /// <summary>
    /// Gets or sets the style; the renderer defaults to <see cref="ButtonVariant.Default"/> (<c>variant</c>).
    /// </summary>
    public ButtonVariant? Variant { get; init; }

    /// <summary>
    /// Gets or sets conditions that disable the button while any fails (<c>@checks</c>).
    /// </summary>
    public IReadOnlyList<A2UICheck>? Checks { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetChild("child", Child);
        writer.SetAction("action", Action);
        writer.SetEnum("variant", Variant);
        writer.SetChecks(Checks);
    }
}
