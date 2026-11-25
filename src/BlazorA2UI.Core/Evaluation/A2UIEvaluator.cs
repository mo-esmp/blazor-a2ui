using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Expressions;
using BlazorA2UI.Rpc;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// Evaluates A2UI dynamic values: literals, <c>{"@path"}</c> data bindings and
/// <c>{"@call"}</c> function calls, with catalog resolution and the v1.0 caller
/// boundaries (<c>allowedCallers</c>, <c>requiresUserActivation</c>).
/// </summary>
public sealed class A2UIEvaluator
{
    /// <summary>
    /// Maximum nesting of calls and bindings in one evaluation.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Creates an evaluator.
    /// </summary>
    /// <param name="catalogs">The catalogs whose functions calls may use.</param>
    /// <param name="services">Services passed to function implementations, or null.</param>
    public A2UIEvaluator(CatalogRegistry catalogs, IServiceProvider? services = null)
    {
        Catalogs = catalogs;
        Services = services;
    }

    /// <summary>
    /// Gets the catalogs that detached evaluations resolve functions in. Surface evaluations see only their surface's catalogs.
    /// </summary>
    public CatalogRegistry Catalogs { get; }

    /// <summary>
    /// Gets the services function implementations can use, or null.
    /// </summary>
    public IServiceProvider? Services { get; }

    /// <summary>
    /// Evaluates <paramref name="value"/>. Literals are returned as copies.
    /// </summary>
    public async ValueTask<JsonNode?> EvaluateAsync(JsonNode? value, EvaluationContext context, EvaluationScope scope)
    {
        scope.Depth++;
        try
        {
            if (scope.Depth > MaxDepth)
            {
                throw new A2UIExpressionException($"Expression nesting exceeds the maximum depth of {MaxDepth}.");
            }

            switch (value)
            {
                case null:
                    return null;
                case JsonValue v:
                    return v.DeepClone();
                case JsonArray arr:
                    {
                        JsonArray result = new();
                        foreach (JsonNode? item in arr)
                        {
                            result.Add(await EvaluateAsync(item, context, scope).ConfigureAwait(false));
                        }

                        return result;
                    }

                case JsonObject obj when obj.ContainsKey("@path"):
                    return ResolveBinding(obj, context);
                case JsonObject obj when obj.ContainsKey("@call"):
                    return await CallAsync(obj, context, scope).ConfigureAwait(false);
                case JsonObject obj:
                    {
                        JsonObject result = new();
                        foreach ((string key, JsonNode? child) in obj)
                        {
                            result[UnescapeKey(key)] = await EvaluateAsync(child, context, scope).ConfigureAwait(false);
                        }

                        return result;
                    }

                default:
                    return null;
            }
        }
        finally
        {
            scope.Depth--;
        }
    }

    /// <summary>
    /// Evaluates synchronously for rendering. Work that cannot finish synchronously
    /// marks the scope pending and yields null; <paramref name="onSettled"/> runs when it finishes.
    /// </summary>
    public JsonNode? Evaluate(JsonNode? value, EvaluationContext context, EvaluationScope scope, Action? onSettled = null)
    {
        ValueTask<JsonNode?> task = EvaluateAsync(value, context, scope);
        if (task.IsCompleted)
        {
            return task.GetAwaiter().GetResult();
        }

        scope.Pending = true;
        _ = ObserveAsync(task, onSettled);
        return null;
    }

    /// <summary>
    /// Awaits an asynchronous evaluation that is still running and runs <paramref name="onSettled"/> when it ends, whether it succeeded or failed. A failure is not rethrown; it surfaces on the next evaluation.
    /// </summary>
    private static async Task ObserveAsync(ValueTask<JsonNode?> task, Action? onSettled)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The failure surfaces on the next evaluation.
        }

        onSettled?.Invoke();
    }

    /// <summary>
    /// Escapes reserved keys in plain objects: <c>@@x</c> is the literal key <c>@x</c>, and any other
    /// single-<c>@</c> key is rejected.
    /// </summary>
    public static string UnescapeKey(string key)
    {
        if (key.Length == 0 || key[0] != '@')
        {
            return key;
        }

        if (key.Length >= 2 && key[1] == '@')
        {
            return key[1..];
        }

        throw new A2UIException(
            A2UIErrorCodes.InvalidReservedKey,
            $"The key '{key}' uses the reserved '@' prefix. Escape a literal '@' key by doubling it ('@{key}').");
    }

    /// <summary>
    /// Reads the value an <c>@path</c> binding points to, resolving relative paths against the context's scope. A missing path gives null.
    /// </summary>
    private static JsonNode? ResolveBinding(JsonObject binding, EvaluationContext context)
    {
        if (binding.Count != 1)
        {
            throw new A2UIExpressionException("A data binding object may only contain '@path'.");
        }

        string path = A2UIValues.AsString(binding["@path"])
                   ?? throw new A2UIExpressionException("'@path' must be a string.");
        return context.Data.TryGet(context.Resolve(path), out JsonNode? value) ? value?.DeepClone() : null;
    }

    /// <summary>
    /// Evaluates one <c>@call</c>: resolves its catalog and arguments, checks who may call it, then runs it locally or routes it to the agent.
    /// </summary>
    /// <returns>The function's result, or null while an argument or the call itself is still pending.</returns>
    /// <exception cref="A2UIException">The call is malformed, its catalog or function is unknown, or the function failed.</exception>
    private async ValueTask<JsonNode?> CallAsync(JsonObject call, EvaluationContext context, EvaluationScope scope)
    {
        foreach ((string key, JsonNode? _) in call)
        {
            if (key is not ("@call" or "catalogId" or "args"))
            {
                throw new A2UIExpressionException($"Unexpected key '{key}' in a function call.");
            }
        }

        string? name = A2UIValues.AsString(call["@call"]);
        if (string.IsNullOrEmpty(name))
        {
            throw new A2UIExpressionException("'@call' must be a non-empty string.");
        }

        JsonNode? rawArgs = call["args"];
        if (rawArgs is not null and not JsonObject)
        {
            throw new A2UIExpressionException($"The args of '{name}' must be an object.");
        }

        if (name.StartsWith('@'))
        {
            return await CallSystemFunctionAsync(name, (JsonObject?)rawArgs, context, scope).ConfigureAwait(false);
        }

        string catalogId;
        if (call.ContainsKey("catalogId"))
        {
            catalogId = A2UIValues.AsString(call["catalogId"]) is { Length: > 0 } explicitId
                ? explicitId
                : throw new A2UICatalogException($"The catalogId of the call to '{name}' must be a non-empty string.");
        }
        else
        {
            catalogId = scope.CatalogOverride ?? context.Surface?.CatalogId
                ?? throw new A2UICatalogException(
                    $"The call to '{name}' names no catalogId and its surface has no default catalogId.");
        }

        if (!TryGetCatalog(context, catalogId, out Catalog? catalog))
        {
            throw scope.Mode == EvaluationMode.AgentCall
                ? new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Catalog '{catalogId}' is not registered on this renderer.")
                : new A2UICatalogException($"Catalog not found: {catalogId}");
        }

        catalog.Functions.TryGetValue(name, out FunctionDefinition? definition);
        CheckBoundaries(name, definition, scope);

        bool pendingBefore = scope.Pending;
        bool argsPending = false;
        scope.Pending = false;
        JsonObject args;
        try
        {
            args = await ResolveArgsAsync(name, rawArgs as JsonObject, definition, catalog, context, scope).ConfigureAwait(false);
        }
        finally
        {
            argsPending = scope.Pending;
            scope.Pending = pendingBefore || argsPending;
        }

        if (argsPending)
        {
            // An argument still waits on the agent: the function runs once every argument has its value.
            return null;
        }

        if (definition?.Implementation is { } implementation)
        {
            FunctionInvocation invocation = new(name, catalog.CatalogId, args, context, scope, this);
            if (name == "formatString")
            {
                // Calls inside ${...} resolve against the catalog this call resolved to.
                string? previous = scope.CatalogOverride;
                scope.CatalogOverride = catalog.CatalogId;
                try
                {
                    return await InvokeAsync(implementation, invocation).ConfigureAwait(false);
                }
                finally
                {
                    scope.CatalogOverride = previous;
                }
            }

            return await RunLocalAsync(implementation, invocation, catalog.CatalogId, name, args, context, scope).ConfigureAwait(false);
        }

        if (scope.Mode == EvaluationMode.AgentCall)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Function '{name}' is not registered on this renderer.");
        }

        if (scope.InTemplate)
        {
            throw new A2UIExpressionException($"Function '{name}' in a formatString template has no local implementation.");
        }

        IAgentFunctionRouter router = context.Surface?.AgentRouter
                     ?? throw new A2UIExpressionException($"Function '{name}' has no local implementation and there is no agent to route it to.");
        if (HasNonFiniteNumber(args, 0))
        {
            throw new A2UIExpressionException($"Function '{name}' cannot be sent to the agent: an argument is a number with no JSON form (NaN or infinity).");
        }

        if (scope.IsReactive)
        {
            JsonNode? value = router.GetOrStart(catalog.CatalogId, name, args, scope.RequestedAgentCalls, out bool pending);
            if (pending)
            {
                scope.Pending = true;
            }

            return value;
        }

        return await router.CallAsync(catalog.CatalogId, name, args, scope.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a value contains NaN or an infinity anywhere, up to the nesting limit.
    /// </summary>
    private static bool HasNonFiniteNumber(JsonNode? node, int depth) => depth <= MaxDepth && node switch
    {
        JsonValue v when v.TryGetValue<double>(out double d) => !double.IsFinite(d),
        JsonValue v when v.TryGetValue<float>(out float f) => !float.IsFinite(f),
        JsonObject o => o.Any(p => HasNonFiniteNumber(p.Value, depth + 1)),
        JsonArray a => a.Any(i => HasNonFiniteNumber(i, depth + 1)),
        _ => false,
    };

    /// <summary>
    /// Enforces the call boundary for the evaluation mode: an agent may call only local functions that allow agent callers and need no user activation, and renderer content may not call agent-only functions, or functions that need user activation outside a user action.
    /// </summary>
    /// <exception cref="A2UIException">The call crosses a boundary; nothing has run.</exception>
    private static void CheckBoundaries(string name, FunctionDefinition? definition, EvaluationScope scope)
    {
        if (scope.Mode == EvaluationMode.AgentCall)
        {
            if (definition is null || !definition.IsLocal)
            {
                throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Function '{name}' is not registered on this renderer.");
            }

            if (definition.AllowedCallers == AllowedCallers.RendererOnly)
            {
                throw new A2UIException(
                    A2UIErrorCodes.InvalidFunctionCall,
                    $"Function '{name}' cannot be called by agent (allowedCallers is rendererOnly).");
            }

            if (definition.RequiresUserActivation)
            {
                throw new A2UIException(
                    A2UIErrorCodes.InvalidFunctionCall,
                    $"Function '{name}' requires user activation and cannot be called by the agent.");
            }

            return;
        }

        if (definition?.AllowedCallers == AllowedCallers.AgentOnly)
        {
            throw new A2UIExpressionException($"Function '{name}' is agentOnly and cannot be called by renderer content.");
        }

        if (definition?.RequiresUserActivation == true && !scope.UserActivated)
        {
            throw new A2UIExpressionException(
                $"Function '{name}' requires user activation and can only run from an action.");
        }
    }

    /// <summary>
    /// Evaluates a call's arguments, then applies the function's defaults and checks them against its argument schema.
    /// </summary>
    /// <returns>The resolved arguments. When an argument is still pending, <see cref="EvaluationScope.Pending"/> is set and the caller must not run the function.</returns>
    private async ValueTask<JsonObject> ResolveArgsAsync(
        string name,
        JsonObject? rawArgs,
        FunctionDefinition? definition,
        Catalog catalog,
        EvaluationContext context,
        EvaluationScope scope)
    {
        JsonObject args = new();
        if (rawArgs is not null)
        {
            if (scope.Mode == EvaluationMode.AgentCall)
            {
                // Static nested calls are checked before any argument expression runs.
                PreflightNestedCalls(rawArgs, catalog, context, 0);
            }

            foreach ((string key, JsonNode? raw) in rawArgs)
            {
                args[UnescapeKey(key)] = await EvaluateAsync(raw, context, scope).ConfigureAwait(false);
            }
        }

        if (definition?.ArgsSchema is { } schema)
        {
            ArgsSchema.ApplyDefaultsAndCoerce(schema, args);
            string? problem = ArgsSchema.Validate(schema, args);
            if (problem is not null)
            {
                string message = $"Validation failed for function '{name}': {problem}";
                throw scope.Mode == EvaluationMode.AgentCall
                    ? new A2UIException(A2UIErrorCodes.InvalidFunctionCall, message)
                    : new A2UIExpressionException(message);
            }
        }

        return args;
    }

    /// <summary>
    /// Finds a catalog: through the surface when there is one, so only the catalogs it was
    /// created with are visible, and in the whole registry for detached evaluations.
    /// </summary>
    private bool TryGetCatalog(EvaluationContext context, string catalogId, [MaybeNullWhen(false)] out Catalog catalog) =>
        context.Surface is { } surface ? surface.TryGetCatalog(catalogId, out catalog) : Catalogs.TryGet(catalogId, out catalog);

    /// <summary>
    /// Checks the static nested calls in an agent's <c>callRendererFunction</c> arguments before any of them runs, so a call that crosses a boundary fails without side effects.
    /// </summary>
    private void PreflightNestedCalls(JsonNode? node, Catalog defaultCatalog, EvaluationContext context, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new A2UIException(A2UIErrorCodes.ExecutionError, "Function arguments are nested too deeply.");
        }

        switch (node)
        {
            case JsonObject obj:
                if (A2UIValues.AsString(obj["@call"]) is { } nested && !nested.StartsWith('@'))
                {
                    Catalog? catalog = defaultCatalog;
                    if (A2UIValues.AsString(obj["catalogId"]) is { } id && !TryGetCatalog(context, id, out catalog))
                    {
                        throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Catalog '{id}' is not registered on this renderer.");
                    }

                    catalog.Functions.TryGetValue(nested, out FunctionDefinition? definition);
                    CheckBoundaries(nested, definition, new EvaluationScope(EvaluationMode.AgentCall));
                }

                foreach ((string _, JsonNode? child) in obj)
                {
                    PreflightNestedCalls(child, defaultCatalog, context, depth + 1);
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    PreflightNestedCalls(item, defaultCatalog, context, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Runs a local function for a call. In a binding or check on a surface, a call that does not complete
    /// synchronously is kept in the surface's <see cref="LocalCallCache"/>, so the evaluation that follows its
    /// settling reads the stored value or error instead of starting the call again. New arguments select a new entry.
    /// </summary>
    /// <returns>The function's result.</returns>
    /// <exception cref="A2UIException">The function failed, now or in the stored call.</exception>
    private static async ValueTask<JsonNode?> RunLocalAsync(
        A2UIFunction implementation,
        FunctionInvocation invocation,
        string catalogId,
        string name,
        JsonObject args,
        EvaluationContext context,
        EvaluationScope scope)
    {
        if (!scope.IsReactive || context.Surface is not { } surface)
        {
            return await InvokeAsync(implementation, invocation).ConfigureAwait(false);
        }

        string key = AgentCallManager.CacheKey(catalogId, name, args);
        if (surface.LocalCalls.TryGet(key, out Task<JsonNode?>? stored))
        {
            JsonNode? settled = await stored.ConfigureAwait(false);
            return settled?.DeepClone();
        }

        ValueTask<JsonNode?> run = InvokeAsync(implementation, invocation);
        if (run.IsCompleted)
        {
            return run.GetAwaiter().GetResult();
        }

        Task<JsonNode?> task = run.AsTask();
        surface.LocalCalls.Add(key, task);
        JsonNode? result = await task.ConfigureAwait(false);
        return result?.DeepClone();
    }

    /// <summary>
    /// Runs a local function implementation. Exceptions other than A2UI errors and cancellation become <see cref="A2UIErrorCodes.ExecutionError"/>.
    /// </summary>
    private static async ValueTask<JsonNode?> InvokeAsync(A2UIFunction implementation, FunctionInvocation invocation)
    {
        try
        {
            return await implementation(invocation).ConfigureAwait(false);
        }
        catch (A2UIException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new A2UIException(A2UIErrorCodes.ExecutionError, $"Function '{invocation.Name}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Evaluates a system function (a name starting with <c>@</c>); <c>@index</c> is the only one.
    /// </summary>
    /// <exception cref="A2UIException">The name is unknown, or <c>@index</c> is used outside a collection template.</exception>
    private async ValueTask<JsonNode?> CallSystemFunctionAsync(
        string name,
        JsonObject? rawArgs,
        EvaluationContext context,
        EvaluationScope scope)
    {
        if (name != "@index")
        {
            throw new A2UIExpressionException($"Unknown system function '{name}'.");
        }

        if (!context.InCollectionScope)
        {
            string message = "@index function can only be evaluated inside a collection template iteration scope.";
            throw scope.Mode == EvaluationMode.AgentCall
                ? new A2UIException(A2UIErrorCodes.ExecutionError, message)
                : new A2UIExpressionException(message);
        }

        double offset = 0;
        if (rawArgs is not null)
        {
            foreach ((string key, JsonNode? _) in rawArgs)
            {
                if (key != "offset")
                {
                    throw new A2UIExpressionException($"'@index' does not take an argument named '{key}'.");
                }
            }

            JsonNode? offsetValue = await EvaluateAsync(rawArgs["offset"], context, scope).ConfigureAwait(false);
            if (offsetValue is not null)
            {
                offset = A2UIValues.ToDouble(offsetValue) is { } o && offsetValue.GetValueKind() == JsonValueKind.Number
                    ? o
                    : throw new A2UIExpressionException("The offset of '@index' must be a number.");
            }
        }

        return JsonValue.Create(context.Indices[^1] + offset);
    }
}
