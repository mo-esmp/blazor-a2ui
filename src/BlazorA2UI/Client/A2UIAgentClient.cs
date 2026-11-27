using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BlazorA2UI.Client;

/// <summary>
/// Connects a <see cref="MessageProcessor"/> to an agent endpoint, such as one mapped with <c>MapA2UIAgent</c>: it posts
/// the user's text and the renderer's messages, streams the agent's answer into the processor as it arrives, and keeps
/// the conversation's id between requests.
/// </summary>
/// <remarks>
/// <para>
/// A turn is one request that the agent answers: the user's text, or the renderer's actions and errors. One turn runs
/// at a time and later ones wait in order; actions that wait together go in one request. Agent function calls and
/// renderer function responses do not start a turn: they are sent at once, even while a turn streams.
/// </para>
/// <para>
/// The client processes messages and raises its events on the synchronization context it was created on, which in
/// Blazor is the renderer's, so create it in a component or inject it there. That is why it does not use
/// <c>ConfigureAwait(false)</c>. It sets <see cref="MessageProcessor.Outbound"/> and clears it when disposed.
/// </para>
/// </remarks>
public sealed class A2UIAgentClient : IAsyncDisposable
{
    /// <summary>
    /// The header that carries the conversation's id.
    /// </summary>
    public const string ConversationHeader = "A2UI-Conversation";

    /// <summary>
    /// The longest error detail from the agent that is kept, in characters.
    /// </summary>
    private const int MaxDetailLength = 300;

    /// <summary>
    /// The most bytes of an error response that are read for its detail.
    /// </summary>
    private const int MaxErrorBodyLength = 4096;

    /// <summary>
    /// The longest conversation id accepted from the agent.
    /// </summary>
    private const int MaxConversationIdLength = 128;

    /// <summary>
    /// Asks the browser's fetch to stream the response in WebAssembly instead of buffering it. Other handlers ignore it.
    /// </summary>
    private static readonly HttpRequestOptionsKey<bool> s_browserStreaming = new("WebAssemblyEnableStreamingResponse");

    /// <summary>
    /// Sends the requests.
    /// </summary>
    private readonly HttpClient _http;

    /// <summary>
    /// Receives the agent's messages and sends the renderer's.
    /// </summary>
    private readonly MessageProcessor _processor;

    /// <summary>
    /// The agent's endpoint.
    /// </summary>
    private readonly Uri _endpoint;

    /// <summary>
    /// The media type to ask the agent for.
    /// </summary>
    private readonly MediaTypeWithQualityHeaderValue _accept;

    /// <summary>
    /// The longest line or event accepted, in bytes.
    /// </summary>
    private readonly int _maxLineLength;

    /// <summary>
    /// How long the agent may send nothing.
    /// </summary>
    private readonly TimeSpan _readTimeout;

    /// <summary>
    /// The most turns that may wait.
    /// </summary>
    private readonly int _maxQueuedTurns;

    /// <summary>
    /// The most renderer messages in one request.
    /// </summary>
    private readonly int _maxMessagesPerRequest;

    /// <summary>
    /// The context the client works on, or null to work on the thread pool.
    /// </summary>
    private readonly SynchronizationContext? _context;

    /// <summary>
    /// The delegate set as the processor's <see cref="MessageProcessor.Outbound"/>.
    /// </summary>
    private readonly Func<A2UIOutboundMessage, ValueTask> _outbound;

    /// <summary>
    /// Guards <see cref="_queue"/>, <see cref="_sideRequests"/>, <see cref="_turns"/>, <see cref="_working"/>,
    /// <see cref="_worker"/>, <see cref="_opening"/> and <see cref="_isDisposed"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The turns waiting to run, in order.
    /// </summary>
    private readonly List<PendingTurn> _queue = [];

    /// <summary>
    /// The agent function calls and renderer function responses being sent, which disposal waits for. Finished ones are
    /// removed when the next one is added.
    /// </summary>
    private readonly List<Task> _sideRequests = [];

    /// <summary>
    /// Cancelled when the client is disposed. It is never disposed, because requests still starting may link to it.
    /// </summary>
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>
    /// Cancelled by <see cref="CancelTurns"/>, then replaced. An old one is not disposed, for the same reason as
    /// <see cref="_disposed"/>.
    /// </summary>
    private CancellationTokenSource _turns = new();

    /// <summary>
    /// The task that runs the queued turns.
    /// </summary>
    private Task _worker = Task.CompletedTask;

    /// <summary>
    /// Completed when the request opening the conversation gets its response headers, or null when none is opening it.
    /// </summary>
    private TaskCompletionSource? _opening;

    /// <summary>
    /// Whether <see cref="_worker"/> is running.
    /// </summary>
    private bool _working;

    /// <summary>
    /// Whether the client was disposed.
    /// </summary>
    private bool _isDisposed;

    /// <summary>
    /// The latest turn's state.
    /// </summary>
    private volatile A2UIConnectionState _state;

    /// <summary>
    /// The conversation's id, or null before the agent issues one.
    /// </summary>
    private volatile string? _conversationId;

    /// <summary>
    /// Creates a client and connects it to the processor.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">An option is missing or out of range.</exception>
    /// <param name="http">Sends the requests. The client does not dispose it.</param>
    /// <param name="processor">The renderer's processor.</param>
    /// <param name="options">The options. They are copied, so later changes have no effect.</param>
    public A2UIAgentClient(HttpClient http, MessageProcessor processor, A2UIAgentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _processor = processor;

        // Validate guarantees an endpoint.
        _endpoint = options.Endpoint!;
        _accept = new MediaTypeWithQualityHeaderValue(options.Format == A2UIAgentStreamFormat.EventStream ? "text/event-stream" : "application/jsonl");
        _maxLineLength = options.MaxLineLength;
        _readTimeout = options.ReadTimeout;
        _maxQueuedTurns = options.MaxQueuedTurns;
        _maxMessagesPerRequest = options.MaxMessagesPerRequest;
        _context = SynchronizationContext.Current;
        _outbound = SendOutboundAsync;
        processor.Outbound = _outbound;
    }

    /// <summary>
    /// Raised when <see cref="State"/> changes, on the client's synchronization context.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised with each piece of text the agent sends, on the client's synchronization context. Only server-sent events
    /// carry text (see <see cref="A2UIAgentClientOptions.Format"/>). The text comes from the model: show it as text.
    /// </summary>
    public event Action<string>? TextReceived;

    /// <summary>
    /// Gets where the latest turn stands.
    /// </summary>
    public A2UIConnectionState State => _state;

    /// <summary>
    /// Gets the conversation's id, or null before the agent issues one or after it expired.
    /// </summary>
    public string? ConversationId => _conversationId;

    /// <summary>
    /// Gets why the latest turn failed, written to be shown, or null when it did not fail.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Gets the exception that failed the latest turn, or null, for logs.
    /// </summary>
    public Exception? LastException { get; private set; }

    /// <summary>
    /// Sends the user's text as a turn, after any turn already waiting.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="InvalidOperationException">Too many turns are waiting (see <see cref="A2UIAgentClientOptions.MaxQueuedTurns"/>).</exception>
    /// <returns>
    /// A task that completes when the turn ends. It does not fail: <see cref="State"/> and <see cref="LastError"/> say
    /// how the turn ended.
    /// </returns>
    /// <param name="text">The user's text.</param>
    /// <param name="cancellationToken">
    /// Cancels the turn, while it waits or while it runs. A waiting turn leaves the queue at once, freeing its place.
    /// </param>
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Enqueue(text, null, cancellationToken);
    }

    /// <summary>
    /// Cancels the running turn and drops the waiting ones. Agent and renderer function calls already sent go on.
    /// </summary>
    public void CancelTurns()
    {
        CancellationTokenSource cancelled;
        List<PendingTurn> dropped;
        lock (_gate)
        {
            cancelled = _turns;
            _turns = new CancellationTokenSource();
            dropped = [.. _queue];
            _queue.Clear();
        }

        cancelled.Cancel();
        foreach (PendingTurn turn in dropped)
        {
            turn.End();
        }
    }

    /// <summary>
    /// Disconnects from the processor, cancels every request and drops the waiting turns. Agent function calls still
    /// being sent fail with <see cref="A2UIErrorCodes.Cancelled"/> before it returns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<PendingTurn> dropped;
        Task worker;
        Task[] sideRequests;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            dropped = [.. _queue];
            _queue.Clear();
            worker = _worker;
            sideRequests = [.. _sideRequests];
            _sideRequests.Clear();
        }

        if (_processor.Outbound == _outbound)
        {
            _processor.Outbound = null;
        }

        _disposed.Cancel();
        foreach (PendingTurn turn in dropped)
        {
            turn.End();
        }

        // The worker ends every turn itself and never fails.
        await worker;
        foreach (Task sideRequest in sideRequests)
        {
            try
            {
                await sideRequest;
            }
            catch (Exception)
            {
                // A renderer function response that could not be sent; the processor already reported it.
            }
        }
    }

    /// <summary>
    /// Returns whether a conversation id from the agent has a safe shape: short, and only base64url characters.
    /// </summary>
    /// <param name="id">The id.</param>
    internal static bool IsAcceptableConversationId(string? id)
    {
        if (id is not { Length: > 0 and <= MaxConversationIdLength })
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shortens text from the agent before it is kept in an error.
    /// </summary>
    /// <param name="text">The text.</param>
    private static string Truncate(string text) => text.Length <= MaxDetailLength ? text : string.Concat(text.AsSpan(0, MaxDetailLength), "…");

    /// <summary>
    /// Sends a renderer message: an action or error waits for a turn, anything else is sent at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">Too many turns are waiting; the processor reports the failure.</exception>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <param name="outbound">The message.</param>
    private ValueTask SendOutboundAsync(A2UIOutboundMessage outbound)
    {
        string kind = outbound.Kind;
        if (kind is "action" or "error")
        {
            _ = Enqueue(null, outbound, CancellationToken.None);
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        Task send = RunOnContextAsync(() => SendWithoutTurnAsync(outbound, kind));
        lock (_gate)
        {
            _sideRequests.RemoveAll(static task => task.IsCompleted);
            if (!send.IsCompleted)
            {
                _sideRequests.Add(send);
            }
        }

        return new ValueTask(send);
    }

    /// <summary>
    /// Adds a turn to the queue, or adds a renderer message to the last waiting turn when it has room, and starts the
    /// worker when it is not running.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="InvalidOperationException">Too many turns are waiting.</exception>
    /// <returns>A task that completes when the turn ends.</returns>
    /// <param name="text">The user's text, or null.</param>
    /// <param name="message">A renderer message, or null.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    private Task Enqueue(string? text, A2UIOutboundMessage? message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (message is not null
                && _queue.Count > 0
                && _queue[^1] is { Text: null } last
                && last.Messages.Count < _maxMessagesPerRequest
                && last.CancellationToken == cancellationToken)
            {
                last.Add(message);
                return last.Done.Task;
            }

            if (_queue.Count >= _maxQueuedTurns)
            {
                throw new InvalidOperationException($"{_maxQueuedTurns} turns are already waiting for the agent.");
            }

            PendingTurn turn = new(text, cancellationToken);
            if (message is not null)
            {
                turn.Add(message);
            }

            _queue.Add(turn);
            if (!_working)
            {
                // The worker is posted, never run inline, so it starts after the caller returns and outside this lock.
                _working = true;
                _worker = RunOnContextAsync(RunWorkerAsync, inline: false);
            }

            if (cancellationToken.CanBeCanceled)
            {
                // A token that is already cancelled runs the callback here; the lock is reentrant, so it takes the turn
                // straight back out of the queue.
                turn.Registration = cancellationToken.Register(static state => ((Pending)state!).Client.Withdraw(((Pending)state!).Turn), new Pending(this, turn));
            }

            return turn.Done.Task;
        }
    }

    /// <summary>
    /// Takes a waiting turn out of the queue when its token is cancelled, and ends it. A turn that already started is
    /// left to its run, which sees the same token.
    /// </summary>
    /// <param name="turn">The turn.</param>
    private void Withdraw(PendingTurn turn)
    {
        lock (_gate)
        {
            if (!_queue.Remove(turn))
            {
                return;
            }
        }

        turn.Done.TrySetResult();
    }

    /// <summary>
    /// Runs work on the client's synchronization context, or on the thread pool when it has none.
    /// </summary>
    /// <returns>A task that completes with the work.</returns>
    /// <param name="work">The work.</param>
    /// <param name="inline">Whether to start the work at once when the caller is already on the context.</param>
    private Task RunOnContextAsync(Func<Task> work, bool inline = true)
    {
        if (_context is null)
        {
            return Task.Run(work);
        }

        if (inline && SynchronizationContext.Current == _context)
        {
            return work();
        }

        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            static state =>
            {
                // The state is always the tuple passed below.
                (Func<Task> run, TaskCompletionSource completion) = ((Func<Task>, TaskCompletionSource))state!;
                _ = CompleteAsync(run, completion);
            },
            (work, done));
        return done.Task;
    }

    /// <summary>
    /// Runs work and completes a source with its outcome.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <param name="completion">Completed when the work ends.</param>
    private static async Task CompleteAsync(Func<Task> work, TaskCompletionSource completion)
    {
        try
        {
            await work();
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// Runs the queued turns in order until the queue is empty.
    /// </summary>
    private async Task RunWorkerAsync()
    {
        while (true)
        {
            PendingTurn turn;
            CancellationToken turns;
            lock (_gate)
            {
                if (_queue.Count == 0 || _isDisposed)
                {
                    _working = false;
                    return;
                }

                turn = _queue[0];
                _queue.RemoveAt(0);
                turns = _turns.Token;
            }

            // Outside the lock: disposing waits for a callback that is running, and the callback takes the lock.
            turn.Registration.Dispose();
            try
            {
                await RunTurnAsync(turn, turns);
            }
            catch (Exception)
            {
                // A StateChanged or TextReceived handler threw. The turn is over, so the next one still runs.
            }
            finally
            {
                turn.Done.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Runs one turn: posts it, streams the answer into the processor and records how it ended.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="turns">Cancelled by <see cref="CancelTurns"/>.</param>
    private async Task RunTurnAsync(PendingTurn turn, CancellationToken turns)
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(turn.CancellationToken, turns, _disposed.Token);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        SetState(A2UIConnectionState.Connecting, null);
        try
        {
            await ExchangeAsync(turn.Text, turn.Messages, turn.Metadata, isTurn: true, cancel.Token);
            SetState(A2UIConnectionState.Ended, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!_disposed.IsCancellationRequested)
            {
                SetState(A2UIConnectionState.Cancelled, null);
            }
        }
        catch (Exception ex) when (!_disposed.IsCancellationRequested)
        {
            SetState(A2UIConnectionState.Failed, ex);
        }
    }

    /// <summary>
    /// Sends an agent function call or a renderer function response, which the agent answers without a turn. The
    /// agent answers a call in the same response, so a call that cannot be sent, or whose response ends without its
    /// answer, fails at once instead of waiting for its timeout; one cut short by disposal fails as cancelled.
    /// </summary>
    /// <exception cref="Exception">A renderer function response could not be sent; the processor reports it.</exception>
    /// <param name="outbound">The message.</param>
    /// <param name="kind">The message's kind.</param>
    private async Task SendWithoutTurnAsync(A2UIOutboundMessage outbound, string kind)
    {
        JsonObject message = outbound.Message;
        string? callId = kind == "callAgentFunction" ? A2UIValues.AsString(message[kind]?["functionCallId"]) : null;
        try
        {
            await ExchangeAsync(null, [message], outbound.Metadata, isTurn: false, _disposed.Token);
        }
        catch (Exception ex) when (callId is not null)
        {
            if (_disposed.IsCancellationRequested)
            {
                FailCall(callId, A2UIErrorCodes.Cancelled, "The agent client was disposed.");
            }
            else
            {
                FailCall(callId, A2UIErrorCodes.ExecutionError, Describe(ex));
            }

            return;
        }

        if (callId is not null)
        {
            FailCall(callId, A2UIErrorCodes.ExecutionError, "The agent's answer ended without a response to the call.");
        }
    }

    /// <summary>
    /// Fails an agent call that is still waiting, through the processor as if the agent had answered with the error. A
    /// call that is no longer waiting is left alone, so no answer for an unknown call is reported.
    /// </summary>
    /// <param name="functionCallId">The call's id.</param>
    /// <param name="code">The error's code.</param>
    /// <param name="message">The error's message.</param>
    private void FailCall(string functionCallId, string code, string message)
    {
        if (!_processor.IsAgentCallPending(functionCallId))
        {
            return;
        }

        _processor.Process(new JsonObject
        {
            ["version"] = A2UIProtocol.Version,
            ["agentFunctionResponse"] = new JsonObject
            {
                ["functionCallId"] = functionCallId,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            },
        });
    }

    /// <summary>
    /// Posts a request and streams the answer into the processor.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">The agent refused the request, or its answer broke off, stalled or broke a limit.</exception>
    /// <exception cref="HttpRequestException">The request could not be sent.</exception>
    /// <exception cref="IOException">The connection broke.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <param name="text">The user's text, or null.</param>
    /// <param name="messages">The renderer's messages.</param>
    /// <param name="metadata">
    /// The metadata the processor gave the renderer's messages, with the data model as it was when they were sent; null
    /// for the user's text, which takes the data model as it is now.
    /// </param>
    /// <param name="isTurn">Whether the request is a turn, which moves <see cref="State"/> and raises text.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    private async Task ExchangeAsync(string? text, List<JsonObject> messages, JsonObject? metadata, bool isTurn, CancellationToken cancellationToken)
    {
        TaskCompletionSource? opening = await WaitForConversationAsync(cancellationToken);
        HttpResponseMessage response;
        string? sentId;
        try
        {
            sentId = _conversationId;
            using HttpRequestMessage request = BuildRequest(text, messages, metadata, sentId);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Only an accepted request commits the id: an error response may carry one the agent will not honor.
            if (response.IsSuccessStatusCode
                && response.Headers.TryGetValues(ConversationHeader, out IEnumerable<string>? ids)
                && ids.FirstOrDefault() is { } id
                && IsAcceptableConversationId(id))
            {
                _conversationId = id;
            }
        }
        finally
        {
            // Requests waiting for the conversation go on, with its id or, if this one failed, to open it themselves.
            if (opening is not null)
            {
                lock (_gate)
                {
                    _opening = null;
                }

                opening.TrySetResult();
            }
        }

        using HttpResponseMessage answer = response;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound
                && sentId is not null
                && Interlocked.CompareExchange(ref _conversationId, null, sentId) == sentId)
            {
                // The conversation expired, and the next request starts a new one, which knows nothing of this one.
                ForgetConversation();
            }

            string? detail = await ReadDetailAsync(response, cancellationToken);
            throw new A2UIAgentStreamException($"The agent refused the request ({(int)response.StatusCode}){(detail is null ? "." : ": " + detail)}");
        }

        bool eventStream = IsEventStream(response.Content.Headers.ContentType?.MediaType);

        if (isTurn)
        {
            SetState(A2UIConnectionState.Streaming, null);
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using A2UILineReader reader = new(body, _maxLineLength, _readTimeout);
        if (eventStream)
        {
            await ReadEventsAsync(reader, isTurn, cancellationToken);
            return;
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            _processor.ProcessLine(line);
        }
    }

    /// <summary>
    /// Clears what belonged to an expired conversation: deletes every surface in the processor, which cancels their
    /// agent calls and lets the new conversation reuse their ids, and drops the waiting actions and errors, which name
    /// those surfaces. Waiting text turns stay; they open the new conversation.
    /// </summary>
    private void ForgetConversation()
    {
        List<PendingTurn> dropped = [];
        lock (_gate)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].Text is null)
                {
                    dropped.Add(_queue[i]);
                    _queue.RemoveAt(i);
                }
            }
        }

        foreach (PendingTurn turn in dropped)
        {
            turn.End();
        }

        _processor.DeleteAllSurfaces("The conversation with the agent expired.");
    }

    /// <summary>
    /// Waits while another request opens the conversation, so every request joins the same one. When there is no id
    /// and no request opening one, the caller becomes the opener.
    /// </summary>
    /// <returns>The source to complete once the caller's response headers arrive, when it opens the conversation; otherwise null.</returns>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private async Task<TaskCompletionSource?> WaitForConversationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_conversationId is not null)
                {
                    return null;
                }

                if (_opening is null)
                {
                    _opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _opening;
                }

                wait = _opening.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Builds a request: the text, the messages, and metadata with the renderer's capabilities and the data model of the
    /// surfaces that ask for it.
    /// </summary>
    /// <returns>The request.</returns>
    /// <param name="text">The user's text, or null.</param>
    /// <param name="messages">The renderer's messages.</param>
    /// <param name="outbound">The processor's metadata for the messages, or null to take the data model as it is now.</param>
    /// <param name="conversationId">The conversation's id, or null.</param>
    private HttpRequestMessage BuildRequest(string? text, List<JsonObject> messages, JsonObject? outbound, string? conversationId)
    {
        JsonObject body = new();
        if (text is not null)
        {
            body["text"] = text;
        }

        if (messages.Count > 0)
        {
            JsonArray array = [];
            foreach (JsonObject message in messages)
            {
                array.Add(message.DeepClone());
            }

            body["messages"] = array;
        }

        JsonObject metadata = new() { ["a2uiRendererCapabilities"] = _processor.GetRendererCapabilities() };
        JsonNode? dataModel = outbound is null ? _processor.GetRendererDataModel() : outbound["a2uiRendererDataModel"]?.DeepClone();
        if (dataModel is not null)
        {
            metadata["a2uiRendererDataModel"] = dataModel;
        }

        body["metadata"] = metadata;
        HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(_accept);
        request.Options.Set(s_browserStreaming, true);
        if (conversationId is not null)
        {
            request.Headers.TryAddWithoutValidation(ConversationHeader, conversationId);
        }

        return request;
    }

    /// <summary>
    /// Returns whether a response is server-sent events, or JSON Lines.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">The response is neither.</exception>
    /// <param name="mediaType">The response's media type, or null.</param>
    private static bool IsEventStream(string? mediaType)
    {
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mediaType, "application/jsonl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new A2UIAgentStreamException($"The agent answered with '{Truncate(mediaType ?? "no content type")}', which is neither JSON Lines nor server-sent events.");
    }

    /// <summary>
    /// Reads server-sent events: <c>a2ui</c> events go to the processor, <c>text</c> events to
    /// <see cref="TextReceived"/>, and an <c>error</c> event fails the request. Other events and fields are ignored.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">The agent sent an error event, or an event broke a limit.</exception>
    /// <param name="reader">Reads the body.</param>
    /// <param name="raiseText">Whether to raise <see cref="TextReceived"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task ReadEventsAsync(A2UILineReader reader, bool raiseText, CancellationToken cancellationToken)
    {
        string? name = null;
        StringBuilder data = new();
        bool hasData = false;
        int dataBytes = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData)
                {
                    Dispatch(name, data.ToString(), raiseText);
                }

                name = null;
                data.Clear();
                hasData = false;
                dataBytes = 0;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            ReadOnlySpan<char> value = colon < 0 ? [] : line.AsSpan(colon + 1);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value[1..];
            }

            if (field == "event")
            {
                name = value.ToString();
            }
            else if (field == "data")
            {
                // The limit counts UTF-8 bytes, as the line limit does: each line's bytes, and one per line break.
                dataBytes += Encoding.UTF8.GetByteCount(value) + (hasData ? 1 : 0);
                if (dataBytes > _maxLineLength)
                {
                    throw new A2UIAgentStreamException($"The agent sent an event longer than {_maxLineLength} bytes.");
                }

                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        // An event the stream ends before its blank line is dropped, as in a browser.
    }

    /// <summary>
    /// Handles one server-sent event.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">The event is an <c>error</c>.</exception>
    /// <param name="name">The event's name, or null.</param>
    /// <param name="data">The event's data.</param>
    /// <param name="raiseText">Whether to raise <see cref="TextReceived"/>.</param>
    private void Dispatch(string? name, string data, bool raiseText)
    {
        switch (name)
        {
            case "a2ui":
                _processor.ProcessLine(data);
                break;
            case "text" when raiseText && ReadString(data, "text") is { } text:
                TextReceived?.Invoke(text);
                break;
            case "error":
                string message = ReadString(data, "message") is { } said ? Truncate(said) : "no details";
                throw new A2UIAgentStreamException($"The agent reported an error: {message}");
        }
    }

    /// <summary>
    /// Reads a string property of a JSON object, or null when the data is not such an object.
    /// </summary>
    /// <param name="json">The JSON.</param>
    /// <param name="property">The property.</param>
    private static string? ReadString(string json, string property)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject obj ? A2UIValues.AsString(obj[property]) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the start of an error response for a problem's <c>detail</c> or <c>title</c>.
    /// </summary>
    /// <returns>The detail, shortened, or null when there is none.</returns>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        byte[] buffer = new byte[MaxErrorBodyLength];
        int length = 0;
        try
        {
            using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
            {
                length += read;
            }

            return JsonNode.Parse(buffer.AsSpan(0, length)) is JsonObject problem
                && (A2UIValues.AsString(problem["detail"]) ?? A2UIValues.AsString(problem["title"])) is { } detail
                ? Truncate(detail)
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Describes a failure for <see cref="LastError"/>.
    /// </summary>
    /// <param name="ex">The failure.</param>
    private static string Describe(Exception ex) => ex switch
    {
        A2UIAgentStreamException stream => stream.Message,
        HttpRequestException or IOException => "The connection to the agent failed.",
        _ => "The turn failed.",
    };

    /// <summary>
    /// Moves to a state and raises <see cref="StateChanged"/>.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="failure">Why the turn failed, for <see cref="A2UIConnectionState.Failed"/>; otherwise null.</param>
    private void SetState(A2UIConnectionState state, Exception? failure)
    {
        LastException = failure;
        LastError = failure is null ? null : Describe(failure);
        _state = state;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A turn waiting to run.
    /// </summary>
    /// <param name="text">The user's text, or null.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    private sealed class PendingTurn(string? text, CancellationToken cancellationToken)
    {
        /// <summary>
        /// Gets the user's text, or null.
        /// </summary>
        public string? Text { get; } = text;

        /// <summary>
        /// Gets the renderer's messages.
        /// </summary>
        public List<JsonObject> Messages { get; } = [];

        /// <summary>
        /// Gets the processor's metadata for the latest message, whose data model is the newest the batch saw; null for
        /// a turn of text only.
        /// </summary>
        public JsonObject? Metadata { get; private set; }

        /// <summary>
        /// Adds a renderer message to the turn. The batch takes the latest message's metadata, so the agent sees the
        /// data model as it was at the last action, not as it is when the request leaves.
        /// </summary>
        /// <param name="message">The message.</param>
        public void Add(A2UIOutboundMessage message)
        {
            Messages.Add(message.Message);
            Metadata = message.Metadata ?? [];
        }

        /// <summary>
        /// Gets the token that cancels the turn.
        /// </summary>
        public CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>
        /// Gets the source completed when the turn ends.
        /// </summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets or sets the registration that takes the turn out of the queue when its token is cancelled.
        /// </summary>
        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>
        /// Ends a turn that was dropped from the queue without running.
        /// </summary>
        public void End()
        {
            Registration.Dispose();
            Done.TrySetResult();
        }
    }

    /// <summary>
    /// The state of a turn's cancellation callback.
    /// </summary>
    /// <param name="Client">The client whose queue holds the turn.</param>
    /// <param name="Turn">The turn.</param>
    private sealed record Pending(A2UIAgentClient Client, PendingTurn Turn);
}
