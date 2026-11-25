namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Modal</c> component. A trigger that opens its content in a dialog.
/// </summary>
public sealed class Modal : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Modal";

    /// <summary>
    /// Gets or sets the component that opens the dialog when the user activates it (<c>trigger</c>).
    /// </summary>
    public required A2UIComponent Trigger { get; init; }

    /// <summary>
    /// Gets or sets the dialog content (<c>content</c>).
    /// </summary>
    public required A2UIComponent Content { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetChild("trigger", Trigger);
        writer.SetChild("content", Content);
    }
}
