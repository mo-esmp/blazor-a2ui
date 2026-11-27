namespace BlazorA2UI.Client;

/// <summary>
/// The format an <see cref="A2UIAgentClient"/> asks the agent to answer in.
/// </summary>
public enum A2UIAgentStreamFormat
{
    /// <summary>
    /// JSON Lines (<c>application/jsonl</c>): one A2UI message per line.
    /// </summary>
    JsonLines,

    /// <summary>
    /// Server-sent events (<c>text/event-stream</c>): A2UI messages, the agent's text, and an error that ends the turn.
    /// </summary>
    EventStream,
}
