using BlazorA2UI.Processing;
using Microsoft.AspNetCore.Http;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// One turn of the agent: what the user sent, and the methods that stream the agent's answer back. Every message is
/// checked the way the renderer will check it before it is sent.
/// </summary>
public sealed class A2UIAgentTurn
{
    /// <summary>
    /// The conversation the turn belongs to.
    /// </summary>
    private readonly A2UIConversation _conversation;

    /// <summary>
    /// Writes the response.
    /// </summary>
    private readonly A2UIStreamWriter _writer;

    /// <summary>
    /// Creates a turn.
    /// </summary>
    /// <param name="httpContext">The request's context.</param>
    /// <param name="conversation">The conversation the turn belongs to.</param>
    /// <param name="writer">Writes the response.</param>
    /// <param name="text">The user's text, or null.</param>
    /// <param name="actions">The renderer's actions.</param>
    /// <param name="errors">The renderer's errors.</param>
    /// <param name="metadata">The request's metadata, or null.</param>
    internal A2UIAgentTurn(
        HttpContext httpContext,
        A2UIConversation conversation,
        A2UIStreamWriter writer,
        string? text,
        IReadOnlyList<A2UIRendererAction> actions,
        IReadOnlyList<A2UIRendererError> errors,
        JsonObject? metadata)
    {
        HttpContext = httpContext;
        _conversation = conversation;
        _writer = writer;
        Text = text;
        Actions = actions;
        Errors = errors;
        Metadata = metadata;
    }

    /// <summary>
    /// Gets the request's context, for the user, services and headers.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <summary>
    /// Gets the conversation's id, which the client sends back with each request.
    /// </summary>
    public string ConversationId => _conversation.Id;

    /// <summary>
    /// Gets the user's text, or null when the turn was started by actions or errors only.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Gets the actions the renderer sent, in order.
    /// </summary>
    public IReadOnlyList<A2UIRendererAction> Actions { get; }

    /// <summary>
    /// Gets the errors the renderer reported, in order. Their messages come from the renderer, so treat them as text.
    /// </summary>
    public IReadOnlyList<A2UIRendererError> Errors { get; }

    /// <summary>
    /// Gets the request's <c>metadata</c> object, or null. It may hold the renderer's data model
    /// (<c>a2uiClientDataModel</c>) or capabilities. It comes from the client, so treat it as untrusted.
    /// </summary>
    public JsonObject? Metadata { get; }

    /// <summary>
    /// Gets whether the client reads server-sent events, which carry text. Over JSON Lines only A2UI messages are sent,
    /// and <see cref="SendTextAsync"/> does nothing.
    /// </summary>
    public bool CanSendText => _writer.IsEventStream;

    /// <summary>
    /// Sends one of the UI messages (<c>createSurface</c>, <c>updateComponents</c>, <c>updateDataModel</c> or
    /// <c>deleteSurface</c>) after checking it the way the renderer will.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The message is not a UI message. Call renderer functions with <see cref="CallRendererFunctionAsync"/>.</exception>
    /// <exception cref="A2UIValidationException">The renderer would reject the message; nothing is sent.</exception>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsUiMessage(message))
        {
            throw new ArgumentException(
                "Only createSurface, updateComponents, updateDataModel and deleteSurface can be sent this way. Use CallRendererFunctionAsync for renderer functions.",
                nameof(message));
        }

        if (await _conversation.SendAsync(message, _writer, cancellationToken).ConfigureAwait(false) is { } error)
        {
            throw new A2UIValidationException($"The renderer would reject the message: {error.Code}: {error.Message}", error.Code);
        }
    }

    /// <summary>
    /// Sends text for the user. Only server-sent events carry text; over JSON Lines it is dropped (see
    /// <see cref="CanSendText"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _writer.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a function on the renderer and waits for its value. The renderer sends the response in a later request
    /// of the same conversation, while this turn is still streaming.
    /// </summary>
    /// <exception cref="A2UIValidationException">
    /// The surface does not exist, so the renderer would reject the call; nothing is sent.
    /// </exception>
    /// <exception cref="A2UIException">
    /// The call is refused before it is sent (see <see cref="A2UIRendererFunctionCalls.Call"/>), or the renderer
    /// answers with an error, or it times out or is cancelled.
    /// </exception>
    /// <returns>The function's value.</returns>
    /// <param name="call">The function call.</param>
    /// <param name="surfaceId">The surface whose data model the call runs against, or null.</param>
    /// <param name="timeout">How long to wait, or null for <see cref="A2UIAgentEndpointOptions.RendererFunctionTimeout"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<JsonNode?> CallRendererFunctionAsync(
        A2UICall call,
        string? surfaceId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        A2UIRendererFunctionCall pending = _conversation.RendererCalls.Call(call, surfaceId, timeout, cancellationToken: cancellationToken);
        A2UIErrorReport? error;
        try
        {
            error = await _conversation.SendCallAsync(pending, surfaceId, _writer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Abandon(pending, A2UIErrorCodes.Cancelled, "The call could not be sent.");
            throw;
        }

        if (error is not null)
        {
            Abandon(pending, error.Code, error.Message);
            throw new A2UIValidationException($"The renderer would reject the call: {error.Code}: {error.Message}", error.Code)
            {
                FunctionCallId = pending.FunctionCallId,
            };
        }

        return await pending.Result.ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a model's answer to the client as it arrives: text goes out as text, and each A2UI message is checked
    /// and sent as soon as its JSON closes. The model should have been prompted with <see cref="A2UIPromptBuilder"/>.
    /// </summary>
    /// <remarks>
    /// Only UI messages are sent. A message the parser cannot read, one that is not a UI message, and one the renderer
    /// would reject are not sent; they are returned as errors, written so they can be passed back to the model.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> is null.</exception>
    /// <returns>The problems found in the answer, in order; empty when everything was sent.</returns>
    /// <param name="chunks">The model's answer, chunk by chunk.</param>
    /// <param name="options">The parser's options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    public async Task<IReadOnlyList<A2UIErrorPart>> SendModelResponseAsync(
        IAsyncEnumerable<string> chunks,
        A2UIResponseParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        A2UIResponseStream stream = new(options);
        List<A2UIErrorPart> errors = [];
        await foreach (string chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await SendPartsAsync(stream.Append(chunk), errors, cancellationToken).ConfigureAwait(false);
        }

        await SendPartsAsync(stream.Complete(), errors, cancellationToken).ConfigureAwait(false);
        return errors;
    }

    /// <summary>
    /// Fails a renderer function call that was not sent, so it stops counting as pending and does not wait for its
    /// timeout.
    /// </summary>
    /// <param name="pending">The call.</param>
    /// <param name="code">The error code to fail it with.</param>
    /// <param name="message">The error message.</param>
    private void Abandon(A2UIRendererFunctionCall pending, string code, string message)
    {
        _conversation.RendererCalls.Resolve(new A2UIFunctionResponse(pending.FunctionCallId, null, new A2UIFunctionError(code, message)));

        // Nothing awaits the failed result, so mark its exception as observed.
        _ = pending.Result.Exception;
    }

    /// <summary>
    /// Returns whether a message is one of the four UI messages.
    /// </summary>
    /// <param name="message">The message.</param>
    private static bool IsUiMessage(JsonObject message)
    {
        string? key = message.Select(p => p.Key).FirstOrDefault(k => k != "version");
        return key is not null && (AgentMessageNames.FromKey(key) & A2UIAgentMessages.Ui) != A2UIAgentMessages.None;
    }

    /// <summary>
    /// Sends the parts the parser returned, collecting the problems.
    /// </summary>
    /// <param name="parts">The parts.</param>
    /// <param name="errors">Receives the problems.</param>
    /// <param name="cancellationToken">Cancels the sends.</param>
    private async Task SendPartsAsync(IReadOnlyList<A2UIResponsePart> parts, List<A2UIErrorPart> errors, CancellationToken cancellationToken)
    {
        foreach (A2UIResponsePart part in parts)
        {
            switch (part)
            {
                case A2UITextPart text:
                    await _writer.WriteTextAsync(text.Text, cancellationToken).ConfigureAwait(false);
                    break;
                case A2UIErrorPart error:
                    errors.Add(error);
                    break;
                case A2UIMessagePart { Message: JsonObject message } when !IsUiMessage(message):
                    errors.Add(new A2UIErrorPart("Only createSurface, updateComponents, updateDataModel and deleteSurface messages can be sent; the message was not sent."));
                    break;
                case A2UIMessagePart { Message: JsonObject message }:
                    if (await _conversation.SendAsync(message, _writer, cancellationToken).ConfigureAwait(false) is { } problem)
                    {
                        errors.Add(new A2UIErrorPart($"The renderer would reject an A2UI message, so it was not sent: {problem.Code}: {problem.Message}"));
                    }

                    break;
            }
        }
    }
}
