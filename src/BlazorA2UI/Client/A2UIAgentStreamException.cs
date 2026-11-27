namespace BlazorA2UI.Client;

/// <summary>
/// The agent's response could not be read: it broke off, stalled, or broke a limit. The message is safe to show.
/// </summary>
public sealed class A2UIAgentStreamException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    public A2UIAgentStreamException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with the error that caused it.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public A2UIAgentStreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
