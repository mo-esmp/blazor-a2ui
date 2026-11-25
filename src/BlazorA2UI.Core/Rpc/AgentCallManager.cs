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
    private readonly Surface _surface;
    private readonly Dictionary<string, ReactiveEntry> _reactive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AgentCallManager(Surface surface)
    {
        _surface = surface;
    }

    public JsonNode? GetOrStart(string catalogId, string name, JsonObject args, ISet<string>? requested, out bool pending)
    {
        var key = CacheKey(catalogId, name, args);
        ReactiveEntry? entry;
        lock (_gate)
        {
            var askedBefore = requested is null || requested.Contains(key);
            requested?.Add(key);
            if (_reactive.TryGetValue(key, out entry) && entry.Call!.Task.IsFaulted && !askedBefore)
            {
                // A binding that never asked for this call gets a fresh attempt after a failure.
                _reactive.Remove(key);
                entry = null;
            }

            if (entry is null)
            {
                entry = new ReactiveEntry { Call = Send(catalogId, name, args) };
                _reactive[key] = entry;
                entry.Call.Task.ContinueWith(
                    t => OnReactiveSettled(key, t),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        var task = entry.Call!.Task;
        if (!task.IsCompleted)
        {
            pending = true;
            return null;
        }

        pending = false;
        if (task.IsCompletedSuccessfully)
        {
            return task.Result?.DeepClone();
        }

        var error = task.Exception?.GetBaseException() as A2UIException
                    ?? new A2UIExpressionException($"The agent call to '{name}' failed.");
        throw error.Code == A2UIErrorCodes.Cancelled
            ? new A2UIException(A2UIErrorCodes.Cancelled, error.Message)
            : new A2UIExpressionException($"Agent function '{name}' failed: {error.Message}");
    }

    public async Task<JsonNode?> CallAsync(string catalogId, string name, JsonObject args, CancellationToken cancellationToken)
    {
        var call = Send(catalogId, name, args);
        using var registration = cancellationToken.Register(() => call.Cancel("The action was cancelled."));
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
            throw new A2UIExpressionException($"Agent function '{name}' failed: {ex.Message}");
        }
    }

    /// <summary>Settles a call with the agent's <c>agentFunctionResponse</c>.</summary>
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

    public bool Owns(string functionCallId)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(functionCallId);
        }
    }

    /// <summary>Cancels every pending call (when the surface is deleted).</summary>
    public void CancelAll(string reason)
    {
        List<PendingCall> calls;
        lock (_gate)
        {
            calls = [.. _pending.Values];
            _pending.Clear();
            _reactive.Clear();
        }

        foreach (var call in calls)
        {
            call.Cancel(reason);
        }
    }

    private PendingCall Send(string catalogId, string name, JsonObject args)
    {
        var processor = _surface.Processor;
        if (!processor.HasOutbound)
        {
            throw new A2UIExpressionException($"Function '{name}' must run on the agent, but the renderer has no connection to the agent.");
        }

        var call = new PendingCall($"fc_{Guid.NewGuid():N}");
        lock (_gate)
        {
            if (_pending.Count >= processor.Options.MaxPendingAgentCalls)
            {
                throw new A2UIExpressionException("Too many agent function calls are waiting for an answer.");
            }

            _pending[call.FunctionCallId] = call;
        }

        var timeout = processor.Options.AgentCallTimeout;
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            _ = Task.Delay(timeout).ContinueWith(
                _ =>
                {
                    bool removed;
                    lock (_gate)
                    {
                        removed = _pending.Remove(call.FunctionCallId);
                    }

                    if (removed)
                    {
                        call.Fail(new A2UIException(A2UIErrorCodes.Timeout, $"The agent did not answer '{name}' in time."));
                    }
                },
                TaskScheduler.Default);
        }

        var message = new JsonObject
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

    internal static string CacheKey(string catalogId, string name, JsonObject args)
    {
        var sb = new StringBuilder();
        sb.Append(catalogId).Append('\n').Append(name).Append('\n');
        WriteCanonical(args, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
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
                for (var i = 0; i < arr.Count; i++)
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

    private sealed class ReactiveEntry
    {
        public PendingCall? Call { get; set; }
    }

    private sealed class PendingCall(string functionCallId)
    {
        private readonly TaskCompletionSource<JsonNode?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string FunctionCallId { get; } = functionCallId;

        public Task<JsonNode?> Task => _tcs.Task;

        public void Succeed(JsonNode? value) => _tcs.TrySetResult(value);

        public void Fail(A2UIException error) => _tcs.TrySetException(error);

        public void Cancel(string reason) =>
            _tcs.TrySetException(new A2UIException(A2UIErrorCodes.Cancelled, reason) { FunctionCallId = FunctionCallId });
    }
}
