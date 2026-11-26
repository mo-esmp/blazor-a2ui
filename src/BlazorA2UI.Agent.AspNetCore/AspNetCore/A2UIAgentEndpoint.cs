using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Serves one mapped A2UI agent endpoint: it reads the client's request, answers the renderer's messages, runs the
/// agent's turn and streams the answer back.
/// </summary>
/// <param name="options">The endpoint's options, already validated.</param>
/// <param name="store">The endpoint's conversations.</param>
/// <param name="handler">Runs the agent's turn.</param>
/// <param name="logger">Logs failures.</param>
internal sealed partial class A2UIAgentEndpoint(
    A2UIAgentEndpointOptions options,
    A2UIConversationStore store,
    A2UIAgentHandler handler,
    ILogger logger)
{
    /// <summary>
    /// What the client sees when the agent's turn fails. Exception details stay in the server's log.
    /// </summary>
    internal const string TurnFailedMessage = "The agent could not finish its answer.";

    /// <summary>
    /// Parses request bodies: no duplicate keys, and nesting bounded by the renderer's depth plus the request's own
    /// two levels (the body and its <c>messages</c> array).
    /// </summary>
    private static readonly JsonDocumentOptions s_bodyOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = JsonChecks.MaxDepth + 3,
    };

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="context">The request's context.</param>
    public async Task HandleAsync(HttpContext context)
    {
        CancellationToken cancellationToken = context.RequestAborted;
        if (!context.Request.HasJsonContentType())
        {
            await ProblemAsync(context, StatusCodes.Status415UnsupportedMediaType, "Send the request as application/json.").ConfigureAwait(false);
            return;
        }

        A2UIAgentRequest request;
        try
        {
            request = await ReadRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            await ProblemAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
            return;
        }

        string? conversationId = context.Request.Headers[A2UIAgentEndpointExtensions.ConversationHeader];
        string? owner = options.ConversationOwner(context);
        A2UIConversation? conversation = conversationId is null ? store.Create(owner) : store.Acquire(conversationId, owner);
        if (conversation is null)
        {
            (int status, string detail) = conversationId is null
                ? (StatusCodes.Status503ServiceUnavailable, "The agent cannot start another conversation now. Try again later.")
                : (StatusCodes.Status404NotFound, "The conversation is unknown or has expired. Start a new one by sending the request without its id.");
            await ProblemAsync(context, status, detail).ConfigureAwait(false);
            return;
        }

        try
        {
            await ServeAsync(context, conversation, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            store.Release(conversation);
        }
    }

    /// <summary>
    /// Answers a request in its conversation.
    /// </summary>
    /// <param name="context">The request's context.</param>
    /// <param name="conversation">The conversation, acquired for this request.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    private async Task ServeAsync(HttpContext context, A2UIConversation conversation, A2UIAgentRequest request, CancellationToken cancellationToken)
    {
        bool startsTurn = request.StartsTurn;
        if (startsTurn && !conversation.TryStartTurn())
        {
            await ProblemAsync(context, StatusCodes.Status409Conflict, "The agent is still answering in this conversation. Send the next request when the answer ends.").ConfigureAwait(false);
            return;
        }

        try
        {
            A2UIStreamWriter writer = new(context.Response.Body, A2UIStreamWriter.WantsEventStream(context.Request));
            HttpResponse response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = writer.MediaType;
            response.Headers.CacheControl = "no-cache, no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers[A2UIAgentEndpointExtensions.ConversationHeader] = conversation.Id;
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await response.StartAsync(cancellationToken).ConfigureAwait(false);

            // Push the headers out now, so the client learns the conversation id before the first message.
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await AnswerAsync(context, conversation, request, writer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The client went away; there is no one to answer.
            }
            catch (Exception ex)
            {
                LogTurnFailed(logger, ex, conversation.LogId);
                await FailAsync(context, writer).ConfigureAwait(false);
            }
        }
        finally
        {
            if (startsTurn)
            {
                conversation.EndTurn();
            }
        }
    }

    /// <summary>
    /// Answers the renderer's messages, then runs the agent's turn when the request starts one.
    /// </summary>
    /// <param name="context">The request's context.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="request">The request.</param>
    /// <param name="writer">Writes the response.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    private async Task AnswerAsync(
        HttpContext context,
        A2UIConversation conversation,
        A2UIAgentRequest request,
        A2UIStreamWriter writer,
        CancellationToken cancellationToken)
    {
        List<A2UIRendererAction> actions = [];
        List<A2UIRendererError> errors = [];
        A2UIRendererMessageProcessor processor = new(options.AgentFunctions)
        {
            OnAction = (action, _) =>
            {
                actions.Add(action);
                return ValueTask.FromResult(true);
            },
            OnError = (error, _) =>
            {
                errors.Add(error);
                return ValueTask.FromResult(true);
            },
            OnRendererFunctionResponse = (response, _) =>
            {
                if (!conversation.RendererCalls.Resolve(response))
                {
                    LogUnmatchedResponse(logger, conversation.LogId);
                }

                return ValueTask.FromResult(true);
            },
        };

        foreach (JsonObject message in request.Messages)
        {
            A2UIRendererMessageResult result = await processor.ProcessAsync(message, cancellationToken).ConfigureAwait(false);
            foreach (JsonObject reply in result.Replies)
            {
                await writer.WriteMessageAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.StartsTurn)
        {
            A2UIAgentTurn turn = new(context, conversation, writer, request.Text, actions, errors, request.Metadata);
            await handler(turn, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells the client that the turn failed: an <c>error</c> event over server-sent events, or an aborted connection
    /// over JSON Lines, so the client never mistakes a cut-off answer for a complete one.
    /// </summary>
    /// <param name="context">The request's context.</param>
    /// <param name="writer">Writes the response.</param>
    private static async Task FailAsync(HttpContext context, A2UIStreamWriter writer)
    {
        if (!writer.IsEventStream)
        {
            context.Abort();
            return;
        }

        try
        {
            await writer.WriteErrorAsync(TurnFailedMessage, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            context.Abort();
        }
    }

    /// <summary>
    /// Reads and checks the request body.
    /// </summary>
    /// <exception cref="BadHttpRequestException">The body is too large, not valid JSON, or not a valid request.</exception>
    /// <returns>The request.</returns>
    /// <param name="httpRequest">The HTTP request.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    private async Task<A2UIAgentRequest> ReadRequestAsync(HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        if (httpRequest.ContentLength > options.MaxRequestBodySize)
        {
            throw TooLarge();
        }

        using MemoryStream copy = new();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await httpRequest.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > options.MaxRequestBodySize)
                {
                    throw TooLarge();
                }

                copy.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(copy.GetBuffer().AsSpan(0, (int)copy.Length), documentOptions: s_bodyOptions);
        }
        catch (JsonException)
        {
            throw Bad("The body is not valid JSON.");
        }

        if (body is not JsonObject root)
        {
            throw Bad("The body must be a JSON object.");
        }

        if (!LenientJson.HasValidStrings(root))
        {
            throw Bad("The body has a string that is not valid Unicode.");
        }

        return A2UIAgentRequest.Read(root, options.MaxMessagesPerRequest);
    }

    /// <summary>
    /// Returns the error for a body over the size limit.
    /// </summary>
    private BadHttpRequestException TooLarge() =>
        new($"The body is larger than {options.MaxRequestBodySize} bytes.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Returns the error for a malformed request.
    /// </summary>
    /// <param name="detail">What is wrong.</param>
    internal static BadHttpRequestException Bad(string detail) => new(detail, StatusCodes.Status400BadRequest);

    /// <summary>
    /// Writes an RFC 9457 problem response.
    /// </summary>
    /// <param name="context">The request's context.</param>
    /// <param name="status">The status code.</param>
    /// <param name="detail">What went wrong, for the client.</param>
    private static Task ProblemAsync(HttpContext context, int status, string detail) =>
        Results.Problem(detail: detail, statusCode: status).ExecuteAsync(context);

    /// <summary>
    /// Logs a turn that failed with an exception.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="conversation">The conversation's fingerprint; the id itself never goes to a log.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "The A2UI agent's turn failed in conversation {Conversation}.")]
    private static partial void LogTurnFailed(ILogger logger, Exception exception, string conversation);

    /// <summary>
    /// Logs a renderer function response that matches no pending call: unknown, late or a duplicate.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="conversation">The conversation's fingerprint; the id itself never goes to a log.</param>
    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "A rendererFunctionResponse in conversation {Conversation} matched no pending call and was ignored.")]
    private static partial void LogUnmatchedResponse(ILogger logger, string conversation);
}
