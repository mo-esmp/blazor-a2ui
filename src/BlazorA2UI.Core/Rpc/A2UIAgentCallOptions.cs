namespace BlazorA2UI.Rpc;

/// <summary>
/// Options for one renderer-initiated agent call made with
/// <see cref="Processing.MessageProcessor.CallAgentFunctionAsync"/>.
/// </summary>
public sealed class A2UIAgentCallOptions
{
    /// <summary>
    /// Gets the <c>functionCallId</c> to send, or null to generate a unique one. An id that a call still
    /// waiting for its answer already uses is refused with <see cref="A2UIErrorCodes.Duplicate"/>.
    /// </summary>
    public string? FunctionCallId { get; init; }

    /// <summary>
    /// Gets how long to wait for the answer, or null to use <see cref="Processing.A2UIProcessorOptions.AgentCallTimeout"/>.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits forever.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
