namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Text</c> component. Displays text. The text is plain: the renderer never interprets it as markup.
/// </summary>
public sealed class Text : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Text";

    /// <summary>
    /// Gets or sets the text. A literal, a binding or a call such as <c>formatString</c> (<c>text</c>).
    /// </summary>
    public required A2UIValue Content { get; init; }

    /// <summary>
    /// Gets or sets the text style; the renderer defaults to <see cref="TextVariant.Body"/> (<c>variant</c>).
    /// </summary>
    public TextVariant? Variant { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("text", Content);
        writer.SetEnum("variant", Variant);
    }
}
