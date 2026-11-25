namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Card</c> component. A container that groups its child visually.
/// </summary>
public sealed class Card : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Card";

    /// <summary>
    /// Gets or sets the content (<c>child</c>).
    /// </summary>
    public required A2UIComponent Child { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.SetChild("child", Child);
    }
}
