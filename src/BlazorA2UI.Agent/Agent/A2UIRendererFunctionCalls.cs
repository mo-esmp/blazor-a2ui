using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Unicode;

namespace BlazorA2UI.Agent;

/// <summary>
/// The agent's calls to renderer functions (<c>callRendererFunction</c>) in one conversation, waiting for their
/// <c>rendererFunctionResponse</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Call"/> builds the message to send and returns it with a task for the function's value, without waiting.
/// <see cref="Resolve(JsonNode)"/> settles the task when the renderer's response arrives:
/// </para>
/// <code>
/// A2UIRendererFunctionCall pending = calls.Call(new A2UICall("getLocation") { CatalogId = catalogId });
/// await SendToRendererAsync(pending.Message);
/// JsonNode? location = await pending.Result;
/// </code>
/// <para>
/// A call's task fails with an <see cref="A2UIException"/> when it times out (<see cref="A2UIErrorCodes.Timeout"/>),
/// when the renderer answers with an error (the renderer's code), when its response is malformed
/// (<see cref="A2UIErrorCodes.UnknownError"/>), or when it is cancelled or the instance is disposed
/// (<see cref="A2UIErrorCodes.Cancelled"/>). Responses to calls that are unknown, already settled, timed out or
/// cancelled are ignored. The instance is safe for concurrent use.
/// </para>
/// </remarks>
public sealed class A2UIRendererFunctionCalls : IDisposable
{
    /// <summary>
    /// The longest timeout a call can have, the most a timer accepts.
    /// </summary>
    private static readonly TimeSpan s_maxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The calls waiting for a response, by id.
    /// </summary>
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The options the instance was created with.
    /// </summary>
    private readonly A2UIRendererFunctionCallsOptions _options;

    /// <summary>
    /// The agent's catalogs by id, or null when calls are not checked against catalogs.
    /// </summary>
    private readonly Dictionary<string, Catalog>? _catalogs;

    /// <summary>
    /// Whether <see cref="Dispose"/> has run.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates the call table for one conversation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="A2UIRendererFunctionCallsOptions.DefaultTimeout"/> is not positive or infinite, or
    /// <see cref="A2UIRendererFunctionCallsOptions.MaxPending"/> is less than 1.
    /// </exception>
    /// <exception cref="ArgumentException">Two of the options' catalogs have the same id.</exception>
    /// <param name="options">The options, or null for the defaults.</param>
    public A2UIRendererFunctionCalls(A2UIRendererFunctionCallsOptions? options = null)
    {
        _options = options ?? new A2UIRendererFunctionCallsOptions();
        CheckTimeout(_options.DefaultTimeout, nameof(options));
        if (_options.MaxPending is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxPending, "MaxPending must be at least 1.");
        }

        if (_options.Catalogs is { } catalogs)
        {
            _catalogs = new Dictionary<string, Catalog>(StringComparer.Ordinal);
            foreach (Catalog catalog in catalogs)
            {
                if (!_catalogs.TryAdd(catalog.CatalogId, catalog))
                {
                    throw new ArgumentException($"Two catalogs have the id '{catalog.CatalogId}'.", nameof(options));
                }
            }
        }
    }

    /// <summary>
    /// Gets the number of calls waiting for a response.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Registers a renderer function call and returns its message and the task of its value. Send the message to the
    /// renderer, then await the task.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The call has no catalog id; its name is not a UAX #31 identifier or starts with <c>@</c>; an argument name is
    /// not an identifier or an argument is not finite JSON; or <paramref name="functionCallId"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is already cancelled.</exception>
    /// <exception cref="A2UIException">
    /// The instance is disposed (<see cref="A2UIErrorCodes.Disposed"/>); the id is already pending
    /// (<see cref="A2UIErrorCodes.Duplicate"/>); <see cref="A2UIRendererFunctionCallsOptions.MaxPending"/> calls are
    /// pending (<see cref="A2UIErrorCodes.TooManyPending"/>). With catalogs: the catalog is not one of them or has no
    /// renderer function of that name (<see cref="A2UIErrorCodes.UnknownFunction"/>), or the agent may not call the
    /// function, because it is <c>rendererOnly</c> or needs user activation, or its arguments fail its schema
    /// (<see cref="A2UIErrorCodes.InvalidFunctionCall"/>). Nothing is registered when a call is refused.
    /// </exception>
    /// <returns>The pending call: its id, its message and the task of its value.</returns>
    /// <param name="call">The function call. Its arguments may hold bindings, which the renderer resolves against the surface's data model.</param>
    /// <param name="surfaceId">The surface whose data model the call runs against, or null to run it against an empty data model.</param>
    /// <param name="timeout">How long to wait for the response, or null for <see cref="A2UIRendererFunctionCallsOptions.DefaultTimeout"/>.</param>
    /// <param name="functionCallId">The call's id, or null to generate one.</param>
    /// <param name="cancellationToken">Cancels the wait: the task then fails with <see cref="A2UIErrorCodes.Cancelled"/> and a late response is ignored.</param>
    public A2UIRendererFunctionCall Call(
        A2UICall call,
        string? surfaceId = null,
        TimeSpan? timeout = null,
        string? functionCallId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (timeout is { } requested)
        {
            CheckTimeout(requested, nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (functionCallId is { Length: 0 })
        {
            throw new ArgumentException("The function call id must not be empty.", nameof(functionCallId));
        }

        if (string.IsNullOrEmpty(call.CatalogId))
        {
            throw new ArgumentException($"The call to '{call.Name}' needs a catalog id.", nameof(call));
        }

        if (call.Name.StartsWith('@') || !Uax31.IsIdentifier(call.Name))
        {
            throw new ArgumentException($"The function name '{call.Name}' must be a UAX #31 identifier that does not start with '@'.", nameof(call));
        }

        foreach (string key in call.Args.Keys)
        {
            if (!Uax31.IsIdentifier(key))
            {
                throw new ArgumentException($"The argument name '{key}' of '{call.Name}' is not a UAX #31 identifier.", nameof(call));
            }
        }

        JsonObject callFunction = call.ToJson();
        JsonObject args = callFunction["args"] as JsonObject ?? new JsonObject();
        if (!JsonChecks.IsSendable(args))
        {
            throw new ArgumentException($"The arguments of '{call.Name}' must be finite JSON within {JsonChecks.MaxDepth} levels.", nameof(call));
        }

        string id = functionCallId ?? "call-" + Guid.NewGuid().ToString("N");
        CheckTarget(call.CatalogId, call.Name, args, id);
        JsonObject body = new();
        if (!string.IsNullOrEmpty(surfaceId))
        {
            body["surfaceId"] = surfaceId;
        }

        body["functionCallId"] = id;
        body["callFunction"] = callFunction;
        JsonObject message = new() { ["version"] = A2UIProtocol.Version, ["callRendererFunction"] = body };
        PendingCall pending = new(call.Name);
        lock (_gate)
        {
            if (_disposed)
            {
                throw new A2UIException(A2UIErrorCodes.Disposed, "The renderer function calls were disposed.") { FunctionCallId = id };
            }

            if (_pending.ContainsKey(id))
            {
                throw new A2UIException(A2UIErrorCodes.Duplicate, $"A call with the id '{id}' is already pending.") { FunctionCallId = id };
            }

            if (_options.MaxPending is { } max && _pending.Count >= max)
            {
                throw new A2UIException(A2UIErrorCodes.TooManyPending, $"{_pending.Count} renderer function calls are already pending, the most allowed.") { FunctionCallId = id };
            }

            _pending[id] = pending;
            TimeSpan wait = timeout ?? _options.DefaultTimeout;
            if (wait != Timeout.InfiniteTimeSpan)
            {
                pending.Timer = _options.TimeProvider.CreateTimer(
                    _ => Fail(id, pending, new A2UIException(A2UIErrorCodes.Timeout, $"Renderer function '{call.Name}' timed out after {wait.TotalSeconds:0.###}s.") { FunctionCallId = id }),
                    null,
                    wait,
                    Timeout.InfiniteTimeSpan);
            }

            if (cancellationToken.CanBeCanceled)
            {
                pending.Registration = cancellationToken.Register(
                    () => Fail(id, pending, new A2UIException(A2UIErrorCodes.Cancelled, $"The call to renderer function '{call.Name}' was cancelled.") { FunctionCallId = id }));
            }
        }

        return new A2UIRendererFunctionCall(id, message, pending.Completion.Task);
    }

    /// <summary>
    /// Settles the call a <c>rendererFunctionResponse</c> message answers. The message comes from the renderer, so this
    /// never throws for it.
    /// </summary>
    /// <remarks>
    /// A malformed response whose <c>functionCallId</c> names a pending call fails that call at once with
    /// <see cref="A2UIErrorCodes.UnknownError"/>, rather than leaving it to time out.
    /// </remarks>
    /// <returns>
    /// True when the response named a pending call, which it settles or fails. False when it is not a
    /// <c>rendererFunctionResponse</c> or no call with its id is pending (unknown, late or a duplicate), so it can go to
    /// the model's turn.
    /// </returns>
    /// <param name="message">The whole message, with its <c>version</c>.</param>
    public bool Resolve(JsonNode? message)
    {
        JsonObject body;
        try
        {
            (string kind, body) = RendererMessageParser.ReadEnvelope(message);
            if (kind != RendererMessageParser.RendererFunctionResponse)
            {
                return false;
            }
        }
        catch (A2UIValidationException)
        {
            return false;
        }

        try
        {
            return Resolve(RendererMessageParser.ReadFunctionResponse(body, RendererMessageParser.RendererFunctionResponse));
        }
        catch (A2UIValidationException ex)
        {
            if (A2UIValues.AsString(body["functionCallId"]) is not { } id)
            {
                return false;
            }

            return Fail(id, null, new A2UIException(A2UIErrorCodes.UnknownError, $"Malformed rendererFunctionResponse: {ex.Message}") { FunctionCallId = id });
        }
    }

    /// <summary>
    /// Settles the call a response answers: with its value, or with an <see cref="A2UIException"/> carrying the
    /// renderer's error code.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    /// <returns>True when a call with the response's id was pending; false when none is (unknown, late or a duplicate).</returns>
    /// <param name="response">The response, as <see cref="A2UIRendererMessageProcessor.OnRendererFunctionResponse"/> receives it.</param>
    public bool Resolve(A2UIFunctionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string id = response.FunctionCallId;
        PendingCall? pending;
        lock (_gate)
        {
            if (!_pending.Remove(id, out pending))
            {
                return false;
            }

            pending.Stop();
        }

        if (response.Error is { } error)
        {
            pending.Completion.TrySetException(
                new A2UIException(error.Code, $"Renderer function '{pending.Name}' failed ({error.Code}): {error.Message}") { FunctionCallId = id });
        }
        else
        {
            pending.Completion.TrySetResult(response.Value);
        }

        return true;
    }

    /// <summary>
    /// Returns whether a call with this id is waiting for its response. A host can use it to take only the responses to
    /// its own calls and pass any other response on to the model's turn.
    /// </summary>
    /// <param name="functionCallId">The id of a response.</param>
    public bool IsPending(string functionCallId)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(functionCallId);
        }
    }

    /// <summary>
    /// Fails every pending call with <see cref="A2UIErrorCodes.Cancelled"/> and refuses later calls with
    /// <see cref="A2UIErrorCodes.Disposed"/>. Later responses are ignored. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        List<KeyValuePair<string, PendingCall>> dropped;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dropped = [.. _pending];
            _pending.Clear();
            foreach (KeyValuePair<string, PendingCall> entry in dropped)
            {
                entry.Value.Stop();
            }
        }

        foreach ((string id, PendingCall pending) in dropped)
        {
            pending.Completion.TrySetException(new A2UIException(A2UIErrorCodes.Cancelled, "The renderer function calls were disposed.") { FunctionCallId = id });
        }
    }

    /// <summary>
    /// Rejects a timeout that is not positive, or infinite, or that a timer cannot hold.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is out of range.</exception>
    /// <param name="timeout">The timeout.</param>
    /// <param name="parameter">The parameter that carried it.</param>
    private static void CheckTimeout(TimeSpan timeout, string parameter)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout > s_maxTimeout))
        {
            throw new ArgumentOutOfRangeException(parameter, timeout, $"The timeout must be positive and at most {s_maxTimeout}, or Timeout.InfiniteTimeSpan.");
        }
    }

    /// <summary>
    /// Checks, when catalogs were given, that the agent may call the function and that its literal arguments match its
    /// schema.
    /// </summary>
    /// <exception cref="A2UIException">The agent may not make the call, as <see cref="Call"/> describes.</exception>
    /// <param name="catalogId">The catalog id.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments, as they go on the wire.</param>
    /// <param name="id">The call's id, for the error.</param>
    private void CheckTarget(string catalogId, string name, JsonObject args, string id)
    {
        if (_catalogs is null)
        {
            return;
        }

        if (!_catalogs.TryGetValue(catalogId, out Catalog? catalog))
        {
            throw new A2UIException(A2UIErrorCodes.UnknownFunction, $"The call to '{name}' names catalog '{catalogId}', which is not one of the agent's catalogs.") { FunctionCallId = id };
        }

        // An agent function runs on the agent, so the renderer cannot run it.
        if (!catalog.Functions.TryGetValue(name, out FunctionDefinition? definition) || _options.AgentFunctions?.Contains(catalogId, name) == true)
        {
            throw new A2UIException(A2UIErrorCodes.UnknownFunction, $"Catalog '{catalogId}' has no renderer function '{name}'.") { FunctionCallId = id };
        }

        if (definition.AllowedCallers == AllowedCallers.RendererOnly)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Function '{name}' is rendererOnly, so the agent may not call it.") { FunctionCallId = id };
        }

        if (definition.RequiresUserActivation)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Function '{name}' requires user activation, and a call from the agent has no user gesture.") { FunctionCallId = id };
        }

        if (definition.ArgsSchema is { } schema && ArgsSchema.ValidateUnresolved(schema, args) is { } problem)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Invalid arguments for function '{name}': {problem}") { FunctionCallId = id };
        }
    }

    /// <summary>
    /// Fails a pending call, if it is still pending.
    /// </summary>
    /// <returns>True when the call was pending.</returns>
    /// <param name="id">The call's id.</param>
    /// <param name="expected">The call to fail, or null for whichever call has the id; a newer call that reuses the id is left alone.</param>
    /// <param name="error">The error.</param>
    private bool Fail(string id, PendingCall? expected, A2UIException error)
    {
        PendingCall? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out pending) || (expected is not null && !ReferenceEquals(pending, expected)))
            {
                return false;
            }

            _pending.Remove(id);
            pending.Stop();
        }

        pending.Completion.TrySetException(error);
        return true;
    }

    /// <summary>
    /// A call waiting for its response.
    /// </summary>
    /// <param name="name">The function's name, for error messages.</param>
    private sealed class PendingCall(string name)
    {
        /// <summary>
        /// Gets the function's name.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Gets the task source of the call's value. Continuations run asynchronously, so settling a call never runs
        /// the caller's code under the lock.
        /// </summary>
        public TaskCompletionSource<JsonNode?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets or sets the timeout timer, or null when the call waits without a limit.
        /// </summary>
        public ITimer? Timer { get; set; }

        /// <summary>
        /// Gets or sets the registration on the caller's cancellation token.
        /// </summary>
        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>
        /// Stops the timer and the cancellation registration. It never waits for a running callback, so it is safe
        /// under the lock that the callbacks also take.
        /// </summary>
        public void Stop()
        {
            Timer?.Dispose();
            Registration.Unregister();
        }
    }
}

/// <summary>
/// A renderer function call that waits for its response.
/// </summary>
/// <param name="FunctionCallId">The call's id.</param>
/// <param name="Message">The <c>callRendererFunction</c> message to send to the renderer.</param>
/// <param name="Result">
/// The function's value. It fails with an <see cref="A2UIException"/> as <see cref="A2UIRendererFunctionCalls"/> describes.
/// </param>
public sealed record A2UIRendererFunctionCall(string FunctionCallId, JsonObject Message, Task<JsonNode?> Result);

/// <summary>
/// Options for <see cref="A2UIRendererFunctionCalls"/>.
/// </summary>
public sealed class A2UIRendererFunctionCallsOptions
{
    /// <summary>
    /// Gets how long a call waits for its response unless the call sets another timeout. The default is 30 seconds;
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a limit.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the most calls that may wait for a response at once, or null for no limit.
    /// </summary>
    public int? MaxPending { get; init; }

    /// <summary>
    /// Gets the agent's catalogs. With them, a call is checked before it is registered: the catalog must have a
    /// renderer function of that name that the agent may call, and the literal arguments must match its schema. Null
    /// checks nothing.
    /// </summary>
    public IEnumerable<Catalog>? Catalogs { get; init; }

    /// <summary>
    /// Gets the agent's own functions, which the renderer cannot run, so a call to one is refused. Used only with
    /// <see cref="Catalogs"/>.
    /// </summary>
    public A2UIAgentFunctions? AgentFunctions { get; init; }

    /// <summary>
    /// Gets the clock for timeouts. The default is the system clock.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
