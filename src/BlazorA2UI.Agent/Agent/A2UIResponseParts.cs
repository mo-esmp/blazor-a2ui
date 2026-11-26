namespace BlazorA2UI.Agent;

/// <summary>
/// A part of a model's answer: text, an A2UI message, or an error in an A2UI JSON block.
/// </summary>
public abstract record A2UIResponsePart;

/// <summary>
/// Text the model wrote outside A2UI JSON blocks. While streaming, consecutive text parts are pieces of one text.
/// </summary>
/// <param name="Text">The text. It is the model's text: show it as text, never as markup.</param>
public sealed record A2UITextPart(string Text) : A2UIResponsePart;

/// <summary>
/// An A2UI message from a block, ready to check with <see cref="A2UIMessageValidator"/> and send to the renderer.
/// </summary>
/// <param name="Message">The message. Its envelope has been checked; its content has not.</param>
public sealed record A2UIMessagePart(JsonObject Message) : A2UIResponsePart;

/// <summary>
/// A block, or a message in it, that could not be read. The rest of the block is skipped, since later messages may
/// depend on the one that failed; messages before it were already returned.
/// </summary>
/// <param name="Error">What went wrong, written so it can be passed back to the model to correct its answer.</param>
public sealed record A2UIErrorPart(string Error) : A2UIResponsePart;

/// <summary>
/// Options for <see cref="A2UIResponseParser"/> and <see cref="A2UIResponseStream"/>.
/// </summary>
public sealed class A2UIResponseParserOptions
{
    /// <summary>
    /// Gets the messages a block may hold. A message of any other kind is an error. The default is
    /// <see cref="A2UIAgentMessages.Ui"/>, matching <see cref="A2UIPromptBuilder.AllowedMessages"/>.
    /// </summary>
    public A2UIAgentMessages AllowedMessages { get; init; } = A2UIAgentMessages.Ui;

    /// <summary>
    /// Gets the longest block accepted, in characters. The default is one million.
    /// </summary>
    public int MaxBlockLength { get; init; } = 1_000_000;

    /// <summary>
    /// Gets the most messages one answer may hold. The default is 1,000.
    /// </summary>
    public int MaxMessages { get; init; } = 1_000;
}
