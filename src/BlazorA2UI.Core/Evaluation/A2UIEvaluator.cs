using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Expressions;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// Evaluates A2UI dynamic values: literals, <c>{"@path"}</c> data bindings and
/// <c>{"@call"}</c> function calls, with catalog resolution and the v1.0 caller
/// boundaries (<c>allowedCallers</c>, <c>requiresUserActivation</c>).
/// </summary>
public sealed class A2UIEvaluator
{
    /// <summary>Maximum nesting of calls and bindings in one evaluation.</summary>
    public const int MaxDepth = 64;

    public A2UIEvaluator(CatalogRegistry catalogs, IServiceProvider? services = null)
    {
        Catalogs = catalogs;
        Services = services;
    }

    public CatalogRegistry Catalogs { get; }

    public IServiceProvider? Services { get; }

    /// <summary>Evaluates <paramref name="value"/>. Literals are returned as copies.</summary>
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
                    var result = new JsonArray();
                    foreach (var item in arr)
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
                    var result = new JsonObject();
                    foreach (var (key, child) in obj)
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
        var task = EvaluateAsync(value, context, scope);
        if (task.IsCompleted)
        {
            return task.GetAwaiter().GetResult();
        }

        scope.Pending = true;
        _ = ObserveAsync(task, onSettled);
        return null;
    }

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

    private static JsonNode? ResolveBinding(JsonObject binding, EvaluationContext context)
    {
        if (binding.Count != 1)
        {
            throw new A2UIExpressionException("A data binding object may only contain '@path'.");
        }

        var path = A2UIValues.AsString(binding["@path"])
                   ?? throw new A2UIExpressionException("'@path' must be a string.");
        return context.Data.TryGet(context.Resolve(path), out var value) ? value?.DeepClone() : null;
    }

    private async ValueTask<JsonNode?> CallAsync(JsonObject call, EvaluationContext context, EvaluationScope scope)
    {
        foreach (var (key, _) in call)
        {
            if (key is not ("@call" or "catalogId" or "args"))
            {
                throw new A2UIExpressionException($"Unexpected key '{key}' in a function call.");
            }
        }

        var name = A2UIValues.AsString(call["@call"]);
        if (string.IsNullOrEmpty(name))
        {
            throw new A2UIExpressionException("'@call' must be a non-empty string.");
        }

        var rawArgs = call["args"];
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

        if (!TryGetCatalog(context, catalogId, out var catalog))
        {
            throw scope.Mode == EvaluationMode.AgentCall
                ? new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Catalog '{catalogId}' is not registered on this renderer.")
                : new A2UICatalogException($"Catalog not found: {catalogId}");
        }

        catalog.Functions.TryGetValue(name, out var definition);
        CheckBoundaries(name, definition, scope);

        var pendingBefore = scope.Pending;
        var argsPending = false;
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
            var invocation = new FunctionInvocation(name, catalog.CatalogId, args, context, scope, this);
            if (name == "formatString")
            {
                // Calls inside ${...} resolve against the catalog this call resolved to.
                var previous = scope.CatalogOverride;
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

            return await InvokeAsync(implementation, invocation).ConfigureAwait(false);
        }

        if (scope.Mode == EvaluationMode.AgentCall)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Function '{name}' is not registered on this renderer.");
        }

        if (scope.InTemplate)
        {
            throw new A2UIExpressionException($"Function '{name}' in a formatString template has no local implementation.");
        }

        var router = context.Surface?.AgentRouter
                     ?? throw new A2UIExpressionException($"Function '{name}' has no local implementation and there is no agent to route it to.");
        if (HasNonFiniteNumber(args, 0))
        {
            throw new A2UIExpressionException($"Function '{name}' cannot be sent to the agent: an argument is a number with no JSON form (NaN or infinity).");
        }

        if (scope.IsReactive)
        {
            var value = router.GetOrStart(catalog.CatalogId, name, args, scope.RequestedAgentCalls, out var pending);
            if (pending)
            {
                scope.Pending = true;
            }

            return value;
        }

        return await router.CallAsync(catalog.CatalogId, name, args, scope.CancellationToken).ConfigureAwait(false);
    }

    private static bool HasNonFiniteNumber(JsonNode? node, int depth) => depth <= MaxDepth && node switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => !double.IsFinite(d),
        JsonValue v when v.TryGetValue<float>(out var f) => !float.IsFinite(f),
        JsonObject o => o.Any(p => HasNonFiniteNumber(p.Value, depth + 1)),
        JsonArray a => a.Any(i => HasNonFiniteNumber(i, depth + 1)),
        _ => false,
    };

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

    private async ValueTask<JsonObject> ResolveArgsAsync(
        string name,
        JsonObject? rawArgs,
        FunctionDefinition? definition,
        Catalog catalog,
        EvaluationContext context,
        EvaluationScope scope)
    {
        var args = new JsonObject();
        if (rawArgs is not null)
        {
            if (scope.Mode == EvaluationMode.AgentCall)
            {
                // Static nested calls are checked before any argument expression runs.
                PreflightNestedCalls(rawArgs, catalog, context, 0);
            }

            foreach (var (key, raw) in rawArgs)
            {
                args[UnescapeKey(key)] = await EvaluateAsync(raw, context, scope).ConfigureAwait(false);
            }
        }

        if (definition?.ArgsSchema is { } schema)
        {
            ArgsSchema.ApplyDefaultsAndCoerce(schema, args);
            var problem = ArgsSchema.Validate(schema, args);
            if (problem is not null)
            {
                var message = $"Validation failed for function '{name}': {problem}";
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
    private bool TryGetCatalog(EvaluationContext context, string catalogId, out Catalog catalog) =>
        context.Surface is { } surface ? surface.TryGetCatalog(catalogId, out catalog) : Catalogs.TryGet(catalogId, out catalog);

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
                    var catalog = defaultCatalog;
                    if (A2UIValues.AsString(obj["catalogId"]) is { } id && !TryGetCatalog(context, id, out catalog))
                    {
                        throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Catalog '{id}' is not registered on this renderer.");
                    }

                    catalog.Functions.TryGetValue(nested, out var definition);
                    CheckBoundaries(nested, definition, new EvaluationScope(EvaluationMode.AgentCall));
                }

                foreach (var (_, child) in obj)
                {
                    PreflightNestedCalls(child, defaultCatalog, context, depth + 1);
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    PreflightNestedCalls(item, defaultCatalog, context, depth + 1);
                }

                break;
        }
    }

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
            var message = "@index function can only be evaluated inside a collection template iteration scope.";
            throw scope.Mode == EvaluationMode.AgentCall
                ? new A2UIException(A2UIErrorCodes.ExecutionError, message)
                : new A2UIExpressionException(message);
        }

        double offset = 0;
        if (rawArgs is not null)
        {
            foreach (var (key, _) in rawArgs)
            {
                if (key != "offset")
                {
                    throw new A2UIExpressionException($"'@index' does not take an argument named '{key}'.");
                }
            }

            var offsetValue = await EvaluateAsync(rawArgs["offset"], context, scope).ConfigureAwait(false);
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
