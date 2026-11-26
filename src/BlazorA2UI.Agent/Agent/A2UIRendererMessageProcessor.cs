using System.Text.Json;

namespace BlazorA2UI.Agent;

/// <summary>
/// Processes the messages a renderer sends to the agent: the agent-side counterpart of the renderer's
/// <see cref="Processing.MessageProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>callAgentFunction</c> is always handled: the processor runs the agent function through
/// <paramref name="agentFunctions"/> and replies with exactly one <c>agentFunctionResponse</c>, which carries the
/// handler's value or an error (<see cref="A2UIErrorCodes.UnknownFunction"/>,
/// <see cref="A2UIErrorCodes.InvalidFunctionCall"/>, <see cref="A2UIErrorCodes.ExecutionError"/>, or the code of an
/// <see cref="A2UIException"/> the handler threw). Without a registry, every call is answered with
/// <see cref="A2UIErrorCodes.UnknownFunction"/>.
/// </para>
/// <para>
/// <c>action</c>, <c>error</c> and <c>rendererFunctionResponse</c> go to <see cref="OnAction"/>, <see cref="OnError"/>
/// and <see cref="OnRendererFunctionResponse"/>. The processor checks the message, passes the callback a typed copy
/// and replies with nothing. A callback that returns false declines the message. A declined message, or one without a
/// callback, is reported as not handled, so the host can pass it to the model's turn; a message without a callback is
/// not checked.
/// </para>
/// <para>
/// The processor keeps no state about a message once it is processed, and is safe for concurrent use.
/// </para>
/// </remarks>
/// <param name="agentFunctions">The agent functions the renderer may call, or null for none.</param>
public sealed class A2UIRendererMessageProcessor(A2UIAgentFunctions? agentFunctions = null)
{
    /// <summary>
    /// The agent functions, or an empty registry.
    /// </summary>
    private readonly A2UIAgentFunctions _agentFunctions = agentFunctions ?? new A2UIAgentFunctions([]);

    /// <summary>
    /// Gets the callback for <c>action</c> messages. It returns false to decline the message. Null leaves actions unhandled.
    /// </summary>
    public Func<A2UIRendererAction, CancellationToken, ValueTask<bool>>? OnAction { get; init; }

    /// <summary>
    /// Gets the callback for <c>error</c> messages. It returns false to decline the message. Null leaves errors unhandled.
    /// </summary>
    public Func<A2UIRendererError, CancellationToken, ValueTask<bool>>? OnError { get; init; }

    /// <summary>
    /// Gets the callback for <c>rendererFunctionResponse</c> messages. It returns false to decline the message, as
    /// <see cref="A2UIRendererFunctionCalls.Resolve(A2UIFunctionResponse)"/> does for a response to a call it did not
    /// make. Null leaves responses unhandled.
    /// </summary>
    public Func<A2UIFunctionResponse, CancellationToken, ValueTask<bool>>? OnRendererFunctionResponse { get; init; }

    /// <summary>
    /// Processes one renderer-to-agent message.
    /// </summary>
    /// <exception cref="A2UIValidationException">
    /// The message is not one v1.0 renderer-to-agent message, a <c>callAgentFunction</c> has no
    /// <c>functionCallId</c> to answer, or a message for a callback fails its schema. Nothing ran.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing is replied.</exception>
    /// <returns>Whether the message was handled, and the replies to send to the renderer.</returns>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels a running agent function or callback.</param>
    public async Task<A2UIRendererMessageResult> ProcessAsync(JsonNode? message, CancellationToken cancellationToken = default)
    {
        (string kind, JsonObject body) = RendererMessageParser.ReadEnvelope(message);
        switch (kind)
        {
            case RendererMessageParser.CallAgentFunction:
                JsonObject reply = await _agentFunctions.HandleAsync(body, cancellationToken).ConfigureAwait(false);
                return new A2UIRendererMessageResult(true, [reply]);
            case RendererMessageParser.Action:
                return OnAction is null
                    ? A2UIRendererMessageResult.NotHandled
                    : Handled(await OnAction(RendererMessageParser.ReadAction(body), cancellationToken).ConfigureAwait(false));
            case RendererMessageParser.Error:
                return OnError is null
                    ? A2UIRendererMessageResult.NotHandled
                    : Handled(await OnError(RendererMessageParser.ReadError(body), cancellationToken).ConfigureAwait(false));
            default:
                return OnRendererFunctionResponse is null
                    ? A2UIRendererMessageResult.NotHandled
                    : Handled(await OnRendererFunctionResponse(
                        RendererMessageParser.ReadFunctionResponse(body, RendererMessageParser.RendererFunctionResponse),
                        cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Processes one line of a JSON Lines stream from the renderer.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="A2UIValidationException">The line is not JSON, or the message is invalid as <see cref="ProcessAsync"/> describes.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing is replied.</exception>
    /// <returns>Whether the message was handled, and the replies to send to the renderer.</returns>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancels a running agent function or callback.</param>
    public Task<A2UIRendererMessageResult> ProcessLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = JsonChecks.MaxDepth + 2 });
        }
        catch (JsonException ex)
        {
            throw new A2UIValidationException($"The line is not valid JSON: {ex.Message}");
        }

        return ProcessAsync(message, cancellationToken);
    }

    /// <summary>
    /// Returns the result of a message a callback received.
    /// </summary>
    /// <param name="accepted">What the callback returned: false declines the message.</param>
    private static A2UIRendererMessageResult Handled(bool accepted) =>
        accepted ? A2UIRendererMessageResult.HandledWithoutReplies : A2UIRendererMessageResult.NotHandled;
}

/// <summary>
/// What processing one renderer message did.
/// </summary>
/// <param name="Handled">
/// Whether the processor handled the message. A message it did not handle, such as an <c>action</c> without a callback,
/// is for the agent's turn.
/// </param>
/// <param name="Replies">The messages to send back to the renderer, in order.</param>
public sealed record A2UIRendererMessageResult(bool Handled, IReadOnlyList<JsonObject> Replies)
{
    /// <summary>
    /// Gets the result of a message that was not handled.
    /// </summary>
    internal static A2UIRendererMessageResult NotHandled { get; } = new(false, []);

    /// <summary>
    /// Gets the result of a message a callback handled.
    /// </summary>
    internal static A2UIRendererMessageResult HandledWithoutReplies { get; } = new(true, []);
}
