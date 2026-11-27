namespace BlazorA2UI.Client;

/// <summary>
/// Where an <see cref="A2UIAgentClient"/>'s latest turn stands.
/// </summary>
public enum A2UIConnectionState
{
    /// <summary>
    /// No turn has run yet.
    /// </summary>
    Idle,

    /// <summary>
    /// A turn's request is sent and its answer has not started.
    /// </summary>
    Connecting,

    /// <summary>
    /// The agent's answer is arriving.
    /// </summary>
    Streaming,

    /// <summary>
    /// The latest turn finished.
    /// </summary>
    Ended,

    /// <summary>
    /// The latest turn was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The latest turn failed; <see cref="A2UIAgentClient.LastError"/> says why.
    /// </summary>
    Failed,
}
