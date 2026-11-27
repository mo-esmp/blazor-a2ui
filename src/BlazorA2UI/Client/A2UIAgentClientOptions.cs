namespace BlazorA2UI.Client;

/// <summary>
/// Options for an <see cref="A2UIAgentClient"/>.
/// </summary>
public sealed class A2UIAgentClientOptions
{
    /// <summary>
    /// Gets or sets the agent endpoint's absolute <c>http</c> or <c>https</c> URL, such as one mapped with
    /// <c>MapA2UIAgent</c>. It comes from the host app, never from a message.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the format to ask the agent for. The default is <see cref="A2UIAgentStreamFormat.JsonLines"/>; ask
    /// for <see cref="A2UIAgentStreamFormat.EventStream"/> to receive the agent's text as well.
    /// </summary>
    public A2UIAgentStreamFormat Format { get; set; } = A2UIAgentStreamFormat.JsonLines;

    /// <summary>
    /// Gets or sets the longest line, or server-sent event, accepted from the agent, in bytes. The default is 1 MiB.
    /// A longer one fails the turn.
    /// </summary>
    public int MaxLineLength { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets how long the agent may send nothing before the turn fails. The default is 2 minutes;
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits for ever.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the most turns that may wait while one runs. The default is 16. A turn beyond it is refused.
    /// </summary>
    public int MaxQueuedTurns { get; set; } = 16;

    /// <summary>
    /// Gets or sets the most renderer messages, such as actions, sent in one request. The default is 100, the
    /// endpoint's default limit.
    /// </summary>
    public int MaxMessagesPerRequest { get; set; } = 100;

    /// <summary>
    /// Checks that the options can be used.
    /// </summary>
    /// <exception cref="InvalidOperationException">An option is missing or out of range.</exception>
    internal void Validate()
    {
        if (Endpoint is not { IsAbsoluteUri: true } endpoint || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The A2UI agent client needs an absolute http or https Endpoint.");
        }

        if (MaxLineLength <= 0 || MaxQueuedTurns <= 0 || MaxMessagesPerRequest <= 0)
        {
            throw new InvalidOperationException("The A2UI agent client's limits must be positive.");
        }

        if (ReadTimeout != Timeout.InfiniteTimeSpan && (ReadTimeout <= TimeSpan.Zero || ReadTimeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new InvalidOperationException("The A2UI agent client's ReadTimeout must be positive and fit a timer, or be infinite.");
        }

        if (!Enum.IsDefined(Format))
        {
            throw new InvalidOperationException("The A2UI agent client's Format is not a known format.");
        }
    }
}
