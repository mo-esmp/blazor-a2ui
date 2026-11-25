using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Rpc;

/// <summary>
/// Renderer-initiated calls to agent functions (<c>callAgentFunction</c>) for one surface.
/// Reactive calls (bindings, checks) with identical arguments share one request and reuse
/// its answer; action calls always send a new request.
/// </summary>
internal sealed class AgentCallManager : IAgentFunctionRouter
{
    /// <summary>
    /// The surface whose calls this manager routes.
    /// </summary>
    private readonly Surface _surface;
    /// <summary>
    /// Calls made by bindings, by call key, so equal calls share one request.
    /// </summary>
    private readonly Dictionary<string, ReactiveEntry> _reactive = new(StringComparer.Ordinal);
    /// <summary>
    /// Calls waiting for an answer, by function call id.
    /// </summary>
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    /// <summary>
    /// Guards the call maps. It is the processor's agent-call gate, shared by every surface, so reserving an id
    /// and registering its call take one lock and no two locks can be taken in opposite orders.
    /// </summary>
    private readonly object _gate;

    /// <summary>
    /// Creates the manager for <paramref name="surface"/>.
    /// </summary>
    public AgentCallManager(Surface surface)
    {
        _surface = surface;
        _gate = surface.Processor.AgentCallGate;
    }

    /// <summary>
    /// Returns the agent's answer to a binding's call, starting the call if needed.
    /// </summary>
    /// <param name="catalogId">The catalog the function belongs to.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The resolved arguments.</param>
    /// <param name="requested">The calls the binding has asked for before, or null; a failed call is sent again only for a binding that never asked for it.</param>
    /// <param name="pending">Set when the answer has not arrived; the result is then null.</param>
    /// <exception cref="A2UIException">The call failed or was cancelled, or it could not be sent.</exception>
    public JsonNode? GetOrStart(string catalogId, string name, JsonObject args, ISet<string>? requested, out bool pending)
    {
        string key = CacheKey(catalogId, name, args);
        ReactiveEntry? entry;
        lock (_gate)
        {
            bool askedBefore = requested is null || requested.Contains(key);
            requested?.Add(key);
            if (_reactive.TryGetValue(key, out entry) && entry.Call.Task.IsFaulted && !askedBefore)
            {
                // A binding that never asked for this call gets a fresh attempt after a failure.
                _reactive.Remove(key);
                entry = null;
            }

            if (entry is null)
            {
                entry = new ReactiveEntry(Send(catalogId, name, args));
                _reactive[key] = entry;
                entry.Call.Task.ContinueWith(
                    t => OnReactiveSettled(key, t),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        Task<JsonNode?> task = entry.Call.Task;
        if (!task.IsCompleted)
        {
            pending = true;
            return null;
        }

        pending = false;
        if (task.IsCompletedSuccessfully)
        {
            return entry.Call.Value?.DeepClone();
        }

        A2UIException error = task.Exception?.GetBaseException() as A2UIException
                    ?? new A2UIExpressionException($"The agent call to '{name}' failed.");
        throw error.Code == A2UIErrorCodes.Cancelled
            ? new A2UIException(A2UIErrorCodes.Cancelled, error.Message)
            : new A2UIExpressionException($"Agent function '{name}' failed: {error.Message}") { FunctionCallId = entry.Call.FunctionCallId };
    }

    /// <summary>
    /// Calls an agent function from an action and waits for the answer.
    /// </summary>
    /// <exception cref="A2UIException">The call failed, timed out, was cancelled, or could not be sent.</exception>
    public async Task<JsonNode?> CallAsync(string catalogId, string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // Nothing is sent: the agent must not run a function the host has already given up on.
            throw new A2UIException(A2UIErrorCodes.Cancelled, "The action was cancelled.");
        }

        PendingCall call = Send(catalogId, name, args);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancel(call, "The action was cancelled."));
        try
        {
            return await call.Task.ConfigureAwait(false);
        }
        catch (A2UIException ex) when (ex.Code == A2UIErrorCodes.Cancelled)
        {
            throw new A2UIException(A2UIErrorCodes.Cancelled, ex.Message);
        }
        catch (A2UIException ex)
        {
            throw new A2UIExpressionException($"Agent function '{name}' failed: {ex.Message}") { FunctionCallId = call.FunctionCallId };
        }
    }

    /// <summary>
    /// Calls an agent function for the host and waits for the answer, keeping the call's own error codes.
    /// </summary>
    /// <param name="catalogId">The catalog the function belongs to.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments to send.</param>
    /// <param name="options">The call id and timeout, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call; the call then fails with <see cref="A2UIErrorCodes.Cancelled"/>.</param>
    /// <exception cref="A2UIException">
    /// The call could not be sent (<see cref="A2UIErrorCodes.NoListener"/>, <see cref="A2UIErrorCodes.Duplicate"/>,
    /// <see cref="A2UIErrorCodes.Disposed"/>), timed out, was cancelled, or the agent answered with an error, whose code it keeps.
    /// </exception>
    public Task<JsonNode?> CallForHostAsync(
        string catalogId,
        string name,
        JsonObject args,
        A2UIAgentCallOptions? options,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // Nothing is sent: the agent must not run a function the host has already given up on.
            return Task.FromException<JsonNode?>(new A2UIException(A2UIErrorCodes.Cancelled, "The call was cancelled.")
            {
                FunctionCallId = options?.FunctionCallId,
            });
        }

        PendingCall call;
        try
        {
            call = SendCore(catalogId, name, args, options?.FunctionCallId, options?.Timeout);
        }
        catch (A2UIException ex)
        {
            return Task.FromException<JsonNode?>(ex);
        }

        // Without a token the call's own task is returned, so it is complete as soon as the answer is processed.
        return cancellationToken.CanBeCanceled ? WaitAsync(call, cancellationToken) : call.Task;
    }

    /// <summary>
    /// Waits for a host call, cancelling it when <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <exception cref="A2UIException">The call failed, timed out or was cancelled.</exception>
    private async Task<JsonNode?> WaitAsync(PendingCall call, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancel(call, "The call was cancelled."));
        return await call.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Settles a call with the agent's <c>agentFunctionResponse</c>.
    /// </summary>
    public bool TryComplete(string functionCallId, JsonObject response)
    {
        PendingCall? call;
        lock (_gate)
        {
            if (!_pending.Remove(functionCallId, out call))
            {
                return false;
            }
        }

        _surface.Processor.ReleaseAgentCall(functionCallId);
        if (response["error"] is JsonObject error)
        {
            call.Fail(new A2UIException(
                A2UIValues.AsString(error["code"]) ?? A2UIErrorCodes.ExecutionError,
                A2UIValues.AsString(error["message"]) ?? "The agent function failed.")
            {
                FunctionCallId = functionCallId,
            });
        }
        else
        {
            call.Succeed(response["value"]?.DeepClone());
        }

        return true;
    }

    /// <summary>
    /// Cancels every pending call (when the surface is deleted).
    /// </summary>
    public void CancelAll(string reason)
    {
        List<PendingCall> calls;
        lock (_gate)
        {
            calls = [.. _pending.Values];
            _pending.Clear();
            _reactive.Clear();
        }

        foreach (PendingCall call in calls)
        {
            _surface.Processor.ReleaseAgentCall(call.FunctionCallId);
            call.Cancel(reason);
        }
    }

    /// <summary>
    /// Sends a <c>callAgentFunction</c> message for a binding or an action, with a generated id and the default timeout.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The call could not be sent: no connection to the agent, too many calls waiting, or the processor was disposed.</exception>
    private PendingCall Send(string catalogId, string name, JsonObject args)
    {
        try
        {
            return SendCore(catalogId, name, args, functionCallId: null, timeout: null);
        }
        catch (A2UIException ex) when (ex is not A2UIExpressionException)
        {
            throw new A2UIExpressionException(ex.Message);
        }
    }

    /// <summary>
    /// Sends a <c>callAgentFunction</c> message and registers the call, with its timeout.
    /// </summary>
    /// <param name="catalogId">The catalog the function belongs to.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments to send.</param>
    /// <param name="functionCallId">The id to send, or null to generate one.</param>
    /// <param name="timeout">How long to wait, or null for <see cref="A2UIProcessorOptions.AgentCallTimeout"/>.</param>
    /// <exception cref="A2UIException">
    /// Nothing was sent: the processor was disposed (<see cref="A2UIErrorCodes.Disposed"/>), it has no outbound connection
    /// (<see cref="A2UIErrorCodes.NoListener"/>), the id is already waiting (<see cref="A2UIErrorCodes.Duplicate"/>), or too many calls are waiting.
    /// </exception>
    private PendingCall SendCore(string catalogId, string name, JsonObject args, string? functionCallId, TimeSpan? timeout)
    {
        MessageProcessor processor = _surface.Processor;
        if (!processor.HasOutbound)
        {
            throw new A2UIException(A2UIErrorCodes.NoListener, $"Function '{name}' must run on the agent, but the renderer has no connection to the agent.");
        }

        PendingCall call = new(functionCallId ?? $"fc_{Guid.NewGuid():N}");

        // The id is reserved for the whole processor and the call registered in one step, so two sends
        // cannot both claim an id and disposal either refuses this call or finds it and cancels it.
        processor.ReserveAgentCall(call.FunctionCallId, () =>
        {
            lock (_gate)
            {
                if (_pending.Count >= processor.Options.MaxPendingAgentCalls)
                {
                    throw new A2UIException(A2UIErrorCodes.ExecutionError, "Too many agent function calls are waiting for an answer.")
                    {
                        FunctionCallId = call.FunctionCallId,
                    };
                }

                _pending.Add(call.FunctionCallId, call);
            }
        });

        if (TimerDelay(timeout ?? processor.Options.AgentCallTimeout) is { } wait)
        {
            call.StartTimer(wait, () =>
            {
                if (TryRemove(call))
                {
                    call.Fail(new A2UIException(A2UIErrorCodes.Timeout, $"The agent did not answer '{name}' in time.") { FunctionCallId = call.FunctionCallId });
                }
            });
        }

        JsonObject message = new()
        {
            ["version"] = A2UIProtocol.Version,
            ["callAgentFunction"] = new JsonObject
            {
                ["surfaceId"] = _surface.SurfaceId,
                ["functionCallId"] = call.FunctionCallId,
                ["callFunction"] = new JsonObject
                {
                    ["@call"] = name,
                    ["catalogId"] = catalogId,
                    ["args"] = args.DeepClone(),
                },
            },
        };
        processor.SendOutbound(message);
        return call;
    }

    /// <summary>
    /// Cancels one call and frees its pending slot, so a cancelled call no longer counts against
    /// <see cref="A2UIProcessorOptions.MaxPendingAgentCalls"/>. A late answer for it is ignored.
    /// </summary>
    private void Cancel(PendingCall call, string reason)
    {
        TryRemove(call);
        call.Cancel(reason);
    }

    /// <summary>
    /// Removes <paramref name="call"/> from the pending calls and releases its id, but only while the entry
    /// under its id is still this call: a later call that reuses the id is left alone.
    /// </summary>
    /// <param name="call">The call to remove.</param>
    /// <returns>False when the call had already settled; nothing changes then.</returns>
    private bool TryRemove(PendingCall call)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(call.FunctionCallId, out PendingCall? current) || !ReferenceEquals(current, call))
            {
                return false;
            }

            _pending.Remove(call.FunctionCallId);
        }

        _surface.Processor.ReleaseAgentCall(call.FunctionCallId);
        return true;
    }

    /// <summary>
    /// The delay of a call's timeout timer, or null for a call that waits forever: <see cref="Timeout.InfiniteTimeSpan"/>,
    /// zero or negative. A delay beyond <see cref="MessageProcessor.MaxAgentCallTimeout"/> is capped there.
    /// </summary>
    /// <param name="timeout">The configured timeout.</param>
    private static TimeSpan? TimerDelay(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return null;
        }

        return timeout > MessageProcessor.MaxAgentCallTimeout ? MessageProcessor.MaxAgentCallTimeout : timeout;
    }

    /// <summary>
    /// Handles a binding's call settling: a cancelled call is forgotten so it can be sent again; otherwise the surface is told to re-evaluate.
    /// </summary>
    private void OnReactiveSettled(string key, Task<JsonNode?> task)
    {
        if (task.IsCanceled || task.Exception?.GetBaseException() is A2UIException { Code: A2UIErrorCodes.Cancelled })
        {
            // A cancelled reactive call is sent again the next time the binding evaluates, which
            // happens on the surface's next change rather than at once.
            lock (_gate)
            {
                _reactive.Remove(key);
            }

            return;
        }

        if (!_surface.IsDeleted)
        {
            _surface.NotifyChanged();
        }
    }

    /// <summary>
    /// Builds the key that identifies equal calls: catalog, name and the arguments in canonical JSON.
    /// </summary>
    internal static string CacheKey(string catalogId, string name, JsonObject args)
    {
        StringBuilder sb = new();
        sb.Append(catalogId).Append('\n').Append(name).Append('\n');
        WriteCanonical(args, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Writes a value as canonical JSON, with object keys in ordinal order.
    /// </summary>
    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach ((string key, JsonNode? value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(key)).Append(':');
                    WriteCanonical(value, sb);
                }

                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteCanonical(arr[i], sb);
                }

                sb.Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }

    /// <summary>
    /// A binding call shared by every binding that makes the same call.
    /// </summary>
    /// <param name="call">The call sent to the agent.</param>
    private sealed class ReactiveEntry(PendingCall call)
    {
        /// <summary>
        /// Gets the call sent to the agent.
        /// </summary>
        public PendingCall Call { get; } = call;
    }

    /// <summary>
    /// One call sent to the agent, settled by its answer, a timeout or cancellation.
    /// </summary>
    /// <param name="functionCallId">The id sent with the call.</param>
    private sealed class PendingCall(string functionCallId)
    {
        /// <summary>
        /// Completes <see cref="Task"/> when the call settles.
        /// </summary>
        private readonly TaskCompletionSource<JsonNode?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the id sent with the call.
        /// </summary>
        public string FunctionCallId { get; } = functionCallId;

        /// <summary>
        /// Gets the task that completes with the answer or fails with the call's error.
        /// </summary>
        public Task<JsonNode?> Task => _tcs.Task;

        /// <summary>
        /// Guards <see cref="_timer"/>, which settling and starting the timer can touch from different threads.
        /// </summary>
        private readonly object _timerGate = new();

        /// <summary>
        /// The call's timeout timer, or null when it has none or has settled.
        /// </summary>
        private CancellationTokenSource? _timer;

        /// <summary>
        /// Gets the answer, once the call has succeeded.
        /// </summary>
        public JsonNode? Value { get; private set; }

        /// <summary>
        /// Starts the timeout timer, which runs <paramref name="onTimeout"/> after <paramref name="delay"/> unless
        /// the call settles first. A call that has already settled starts no timer.
        /// </summary>
        /// <param name="delay">How long to wait, at most <see cref="MessageProcessor.MaxAgentCallTimeout"/>.</param>
        /// <param name="onTimeout">Fails the call when it is still pending.</param>
        public void StartTimer(TimeSpan delay, Action onTimeout)
        {
            CancellationTokenSource timer = new();
            lock (_timerGate)
            {
                if (_tcs.Task.IsCompleted)
                {
                    timer.Dispose();
                    return;
                }

                // Armed under the gate, so a concurrent settle cannot dispose the timer between these calls.
                _timer = timer;
                timer.Token.Register(onTimeout);
                timer.CancelAfter(delay);
            }
        }

        /// <summary>
        /// Settles the call with the agent's answer; a call that has already settled is unchanged.
        /// </summary>
        public void Succeed(JsonNode? value)
        {
            if (!_tcs.Task.IsCompleted)
            {
                Value = value;
                _tcs.TrySetResult(value);
            }

            StopTimer();
        }

        /// <summary>
        /// Fails the call with <paramref name="error"/>; a call that has already settled is unchanged.
        /// </summary>
        public void Fail(A2UIException error)
        {
            _tcs.TrySetException(error);
            StopTimer();
        }

        /// <summary>
        /// Cancels the call with a <see cref="A2UIErrorCodes.Cancelled"/> error; a call that has already settled is unchanged.
        /// </summary>
        public void Cancel(string reason)
        {
            _tcs.TrySetException(new A2UIException(A2UIErrorCodes.Cancelled, reason) { FunctionCallId = FunctionCallId });
            StopTimer();
        }

        /// <summary>
        /// Disposes the timeout timer so it never fires for a settled call.
        /// </summary>
        private void StopTimer()
        {
            CancellationTokenSource? timer;
            lock (_timerGate)
            {
                timer = _timer;
                _timer = null;
            }

            timer?.Dispose();
        }
    }
}
