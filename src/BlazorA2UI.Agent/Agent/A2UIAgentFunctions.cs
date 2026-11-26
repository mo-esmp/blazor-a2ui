using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.Agent;

/// <summary>
/// The agent functions the renderer may call with <c>callAgentFunction</c>, and the handlers that run them.
/// </summary>
/// <remarks>
/// <para>
/// An agent function is a function that the catalog declares and the renderer does not implement, so the renderer sends
/// its calls to the agent. Declare it in the catalog the renderer and the agent share (in the catalog document, or with
/// <see cref="Catalog.Extend"/>), then register its handler with <see cref="Add"/>. The declaration's argument schema
/// checks every call before the handler runs.
/// </para>
/// <para>
/// Register every handler before the first call is handled. After that, the registry is safe for concurrent use and
/// one registry can serve many conversations: it keeps no state about a call.
/// </para>
/// </remarks>
public sealed class A2UIAgentFunctions
{
    /// <summary>
    /// The agent's catalogs, by id.
    /// </summary>
    private readonly Dictionary<string, Catalog> _catalogs = new(StringComparer.Ordinal);

    /// <summary>
    /// The registered functions, by catalog id and name.
    /// </summary>
    private readonly Dictionary<(string CatalogId, string Name), Registration> _functions = [];

    /// <summary>
    /// The longest a handler may run; <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
    /// </summary>
    private readonly TimeSpan _timeout = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Creates a registry for the functions of the given catalogs.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalogs"/> or one of its catalogs is null.</exception>
    /// <exception cref="ArgumentException">Two catalogs have the same id.</exception>
    /// <param name="catalogs">The catalogs the agent shares with the renderer.</param>
    public A2UIAgentFunctions(IEnumerable<Catalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        foreach (Catalog catalog in catalogs)
        {
            ArgumentNullException.ThrowIfNull(catalog, nameof(catalogs));
            if (!_catalogs.TryAdd(catalog.CatalogId, catalog))
            {
                throw new ArgumentException($"Two catalogs have the id '{catalog.CatalogId}'.", nameof(catalogs));
            }
        }
    }

    /// <summary>
    /// Gets the longest a handler may run before its call is answered with <see cref="A2UIErrorCodes.ExecutionError"/>
    /// and its cancellation token is cancelled. <see cref="Timeout.InfiniteTimeSpan"/>, the default, sets no limit,
    /// because the renderer already times out its calls.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive or <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan Timeout
    {
        get => _timeout;
        init
        {
            if (value != System.Threading.Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive, or Timeout.InfiniteTimeSpan.");
            }

            _timeout = value;
        }
    }

    /// <summary>
    /// Raised when a handler throws an exception other than <see cref="A2UIException"/>. The renderer gets only a
    /// generic message, so log the exception here. An exception a subscriber throws is ignored, so the call is still
    /// answered.
    /// </summary>
    public event Action<A2UIAgentFunctionCall, Exception>? FunctionFailed;

    /// <summary>
    /// Registers the handler of an agent function.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The catalog is not one of the agent's; it does not declare the function; it implements the function, so a
    /// renderer with that catalog runs it itself and never calls the agent; or the function is <c>agentOnly</c>, so a
    /// renderer may never call it.
    /// </exception>
    /// <exception cref="InvalidOperationException">The function already has a handler.</exception>
    /// <returns>This registry, so calls can be chained.</returns>
    /// <param name="catalogId">The catalog that declares the function.</param>
    /// <param name="name">The function's name.</param>
    /// <param name="handler">Runs a call.</param>
    public A2UIAgentFunctions Add(string catalogId, string name, A2UIAgentFunctionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(catalogId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_catalogs.TryGetValue(catalogId, out Catalog? catalog))
        {
            throw new ArgumentException($"'{catalogId}' is not one of the agent's catalogs.", nameof(catalogId));
        }

        if (!catalog.Functions.TryGetValue(name, out FunctionDefinition? definition))
        {
            throw new ArgumentException(
                $"Catalog '{catalogId}' does not declare a function '{name}'. Declare it in the catalog, so the renderer knows to call the agent.",
                nameof(name));
        }

        if (definition.IsLocal)
        {
            throw new ArgumentException(
                $"Catalog '{catalogId}' implements '{name}', so a renderer with that catalog runs it itself and never calls the agent.",
                nameof(name));
        }

        if (definition.AllowedCallers == AllowedCallers.AgentOnly)
        {
            throw new ArgumentException($"Function '{name}' is agentOnly, so a renderer may never call it.", nameof(name));
        }

        if (!_functions.TryAdd((catalogId, name), new Registration(definition, handler)))
        {
            throw new InvalidOperationException($"Function '{name}' of catalog '{catalogId}' already has a handler.");
        }

        return this;
    }

    /// <summary>
    /// Returns whether a function has a handler.
    /// </summary>
    /// <param name="catalogId">The catalog that declares the function.</param>
    /// <param name="name">The function's name.</param>
    public bool Contains(string catalogId, string name) => _functions.ContainsKey((catalogId, name));

    /// <summary>
    /// Answers a <c>callAgentFunction</c> call with exactly one <c>agentFunctionResponse</c> message. Every failure of
    /// the call itself is part of the answer.
    /// </summary>
    /// <exception cref="A2UIValidationException">The call has no non-empty string <c>functionCallId</c>, so it cannot be answered.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing is answered.</exception>
    /// <returns>The <c>agentFunctionResponse</c> message.</returns>
    /// <param name="body">The <c>callAgentFunction</c> body.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    internal async Task<JsonObject> HandleAsync(JsonObject body, CancellationToken cancellationToken)
    {
        string callId = A2UIValues.AsString(body["functionCallId"]) is { Length: > 0 } id
            ? id
            : throw new A2UIValidationException("callAgentFunction needs a non-empty string functionCallId, so it can be answered.") { Path = "/functionCallId" };
        if (Resolve(body, callId, out A2UIAgentFunctionCall? call, out Registration? registration) is { } refusal)
        {
            return refusal;
        }

        // Resolve returns no refusal only after setting both.
        return await RunAsync(call!, registration!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the response for a call that succeeded.
    /// </summary>
    /// <param name="callId">The call's id.</param>
    /// <param name="value">The function's value.</param>
    private static JsonObject Success(string callId, JsonNode? value) => new()
    {
        ["version"] = A2UIProtocol.Version,
        ["agentFunctionResponse"] = new JsonObject { ["functionCallId"] = callId, ["value"] = value },
    };

    /// <summary>
    /// Builds the response for a call that failed.
    /// </summary>
    /// <param name="callId">The call's id.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">The error message.</param>
    private static JsonObject Failure(string callId, string code, string message) => new()
    {
        ["version"] = A2UIProtocol.Version,
        ["agentFunctionResponse"] = new JsonObject
        {
            ["functionCallId"] = callId,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        },
    };

    /// <summary>
    /// Runs a handler so that every exception it throws, even before its first await, ends up in the returned task.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    private static async Task<JsonNode?> InvokeAsync(A2UIAgentFunctionHandler handler, A2UIAgentFunctionCall call, CancellationToken cancellationToken) =>
        await handler(call, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Checks a call against the schema of <c>callAgentFunction</c>, finds its function and checks its arguments.
    /// </summary>
    /// <returns>The error response to send, or null when the call may run.</returns>
    /// <param name="body">The <c>callAgentFunction</c> body.</param>
    /// <param name="callId">The call's id.</param>
    /// <param name="call">Set to the call, with its checked arguments, when it may run.</param>
    /// <param name="registration">Set to the function's registration when the call may run.</param>
    private JsonObject? Resolve(JsonObject body, string callId, out A2UIAgentFunctionCall? call, out Registration? registration)
    {
        call = null;
        registration = null;
        foreach ((string key, JsonNode? _) in body)
        {
            if (key is not ("surfaceId" or "functionCallId" or "callFunction"))
            {
                return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"callAgentFunction has an unexpected key '{key}'.");
            }
        }

        if (A2UIValues.AsString(body["surfaceId"]) is not { Length: > 0 } surfaceId)
        {
            return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, "callAgentFunction needs a non-empty string surfaceId.");
        }

        if (body["callFunction"] is not JsonObject function)
        {
            return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, "callAgentFunction needs a callFunction object.");
        }

        foreach ((string key, JsonNode? _) in function)
        {
            if (key is not ("@call" or "catalogId" or "args"))
            {
                return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"callFunction has an unexpected key '{key}'.");
            }
        }

        if (A2UIValues.AsString(function["@call"]) is not { Length: > 0 } name)
        {
            return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, "callFunction needs a non-empty string '@call'.");
        }

        string? catalogId = null;
        if (function.ContainsKey("catalogId"))
        {
            catalogId = A2UIValues.AsString(function["catalogId"]);
            if (string.IsNullOrEmpty(catalogId))
            {
                return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"The catalogId of the call to '{name}' must be a non-empty string.");
            }
        }

        JsonObject args = new();
        if (function.ContainsKey("args"))
        {
            if (function["args"] is not JsonObject rawArgs)
            {
                return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"The args of the call to '{name}' must be an object.");
            }

            args = (JsonObject)rawArgs.DeepClone();
        }

        if (FindFunction(callId, name, ref catalogId) is { } unknown)
        {
            return unknown;
        }

        // FindFunction returns no error only after it has resolved the catalog id to a registered function.
        Registration found = _functions[(catalogId!, name)];
        if (found.Definition.ArgsSchema is { } schema)
        {
            ArgsSchema.ApplyDefaultsAndCoerce(schema, args);
            if (ArgsSchema.Validate(schema, args) is { } problem)
            {
                return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"Invalid arguments for function '{name}': {problem}");
            }
        }

        call = new A2UIAgentFunctionCall(catalogId!, name, surfaceId, callId, args);
        registration = found;
        return null;
    }

    /// <summary>
    /// Finds the registered function a call names. A call without a catalog id runs the function of that name when
    /// exactly one catalog has one; renderers always send the catalog id.
    /// </summary>
    /// <returns>The error response to send, or null when the function was found.</returns>
    /// <param name="callId">The call's id.</param>
    /// <param name="name">The function's name.</param>
    /// <param name="catalogId">The call's catalog id, or null; set to the catalog of the function found.</param>
    private JsonObject? FindFunction(string callId, string name, ref string? catalogId)
    {
        if (catalogId is null)
        {
            foreach ((string CatalogId, string Name) key in _functions.Keys)
            {
                if (key.Name != name)
                {
                    continue;
                }

                if (catalogId is not null)
                {
                    return Failure(callId, A2UIErrorCodes.InvalidFunctionCall, $"The call to '{name}' has no catalogId, and several catalogs have an agent function of that name.");
                }

                catalogId = key.CatalogId;
            }

            return catalogId is null
                ? Failure(callId, A2UIErrorCodes.UnknownFunction, $"No catalog of the agent has an agent function '{name}'.")
                : null;
        }

        if (!_catalogs.ContainsKey(catalogId))
        {
            return Failure(callId, A2UIErrorCodes.UnknownFunction, $"The agent has no catalog '{catalogId}'.");
        }

        return _functions.ContainsKey((catalogId, name))
            ? null
            : Failure(callId, A2UIErrorCodes.UnknownFunction, $"Catalog '{catalogId}' has no agent function '{name}'.");
    }

    /// <summary>
    /// Runs a checked call within the timeout and builds its response.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <param name="call">The call.</param>
    /// <param name="registration">The function's registration.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    private async Task<JsonObject> RunAsync(A2UIAgentFunctionCall call, Registration registration, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<JsonNode?> run;
        if (_timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            run = InvokeAsync(registration.Handler, call, linked.Token);
        }
        else
        {
            using CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task deadline = Task.Delay(_timeout, delay.Token);

            // The clock starts first and the handler runs on the thread pool, so a handler that blocks before its
            // first await still times out.
            run = Task.Run(() => InvokeAsync(registration.Handler, call, linked.Token), CancellationToken.None);
            Task finished = await Task.WhenAny(run, deadline).ConfigureAwait(false);

            // Stops the timer when the handler finished first. Only this method registers on the delay token.
            delay.Cancel();
            if (finished != run)
            {
                // The handler's own cancellation callbacks may throw or block, so they run on the thread pool and
                // never hold up or break the answer. The handler may still fail after its call is answered; its
                // exception is observed so it is not lost.
                _ = Task.Run(() => CancelAndDispose(linked), CancellationToken.None);
                _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                return Failure(call.FunctionCallId, A2UIErrorCodes.ExecutionError, $"Function '{call.Name}' timed out after {_timeout.TotalSeconds:0.###}s.");
            }
        }

        using CancellationTokenSource finishedSource = linked;

        JsonNode? value;
        try
        {
            value = await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (A2UIException ex)
        {
            return Failure(call.FunctionCallId, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            try
            {
                FunctionFailed?.Invoke(call, ex);
            }
            catch (Exception)
            {
                // A failing log subscriber must not cost the renderer its answer.
            }

            return Failure(call.FunctionCallId, A2UIErrorCodes.ExecutionError, $"Function '{call.Name}' failed.");
        }

        JsonObject reply = Success(call.FunctionCallId, JsonChecks.Detach(value));
        return JsonChecks.IsSendable(reply)
            ? reply
            : Failure(call.FunctionCallId, A2UIErrorCodes.ExecutionError, $"Function '{call.Name}' returned a value that is not finite JSON within the renderer's {JsonChecks.MaxDepth}-level message limit.");
    }

    /// <summary>
    /// Cancels a timed-out handler's token and disposes its source. Exceptions from the handler's cancellation
    /// callbacks are swallowed: the call has already been answered.
    /// </summary>
    /// <param name="source">The handler's token source.</param>
    private static void CancelAndDispose(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A callback the handler registered threw; the call was answered with the timeout already.
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// A registered function: its declaration and its handler.
    /// </summary>
    /// <param name="Definition">The catalog's declaration of the function.</param>
    /// <param name="Handler">Runs a call.</param>
    private sealed record Registration(FunctionDefinition Definition, A2UIAgentFunctionHandler Handler);
}
