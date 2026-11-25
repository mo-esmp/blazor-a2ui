using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Rpc;

namespace BlazorA2UI.Processing;

/// <summary>
/// One A2UI surface: its components (an adjacency list keyed by id), its data model,
/// and the helpers a UI adapter uses to evaluate properties, run checks, write
/// two-way bindings and dispatch actions.
/// </summary>
public sealed class Surface
{
    private readonly Dictionary<string, ComponentInstance> _components = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private int _changeSuspension;
    private bool _changePending;

    internal Surface(MessageProcessor processor, string surfaceId, string? catalogId, bool sendDataModel, JsonObject? metadata)
    {
        Processor = processor;
        SurfaceId = surfaceId;
        CatalogId = catalogId;
        SendDataModel = sendDataModel;
        Metadata = metadata;
        CatalogIds = processor.Catalogs.Catalogs.Select(c => c.CatalogId).ToHashSet(StringComparer.Ordinal);
        DataModel = new DataModel();
        DataModel.Changed += _ => NotifyChanged();
        AgentCalls = new AgentCallManager(this);
    }

    public MessageProcessor Processor { get; }

    /// <summary>The catalogs this surface can use: those registered when it was created.</summary>
    public IReadOnlySet<string> CatalogIds { get; }

    /// <summary>Looks up a catalog this surface can use.</summary>
    internal bool TryGetCatalog(string catalogId, out Catalogs.Catalog catalog)
    {
        catalog = null!;
        return CatalogIds.Contains(catalogId) && Processor.Catalogs.TryGet(catalogId, out catalog);
    }

    public string SurfaceId { get; }

    /// <summary>The surface's default catalog id, from <c>createSurface</c>.</summary>
    public string? CatalogId { get; }

    public bool SendDataModel { get; }

    /// <summary>Surface metadata (<c>metadata.extensions</c>) from <c>createSurface</c>.</summary>
    public JsonObject? Metadata { get; }

    public DataModel DataModel { get; }

    public IReadOnlyDictionary<string, ComponentInstance> Components => _components;

    /// <summary>The root component, once the agent has sent one with id <c>root</c>.</summary>
    public ComponentInstance? Root => _components.GetValueOrDefault("root");

    public bool IsDeleted { get; internal set; }

    internal AgentCallManager AgentCalls { get; }

    /// <summary>Routes calls without a local implementation to the agent.</summary>
    public IAgentFunctionRouter AgentRouter => AgentCalls;

    /// <summary>Raised when components or data change, or an asynchronous value settles.</summary>
    public event Action? Changed;

    /// <summary>The root evaluation context of this surface.</summary>
    public EvaluationContext RootContext => EvaluationContext.ForSurface(this);

    internal void ReplaceComponents(IEnumerable<ComponentInstance> components)
    {
        foreach (var component in components)
        {
            _components[component.Id] = component;
        }

        _reportedErrors.Clear();
        NotifyChanged();
    }

    internal Dictionary<string, ComponentInstance> SnapshotComponents() => new(_components, StringComparer.Ordinal);

    /// <summary>Raises <see cref="Changed"/> (coalesced while updates are batched).</summary>
    public void NotifyChanged()
    {
        if (_changeSuspension > 0)
        {
            _changePending = true;
            return;
        }

        Changed?.Invoke();
    }

    internal IDisposable BatchChanges()
    {
        _changeSuspension++;
        return new Resume(this);
    }

    private sealed class Resume(Surface surface) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--surface._changeSuspension == 0 && surface._changePending)
            {
                surface._changePending = false;
                surface.Changed?.Invoke();
            }
        }
    }

    // ---------------------------------------------------------------- evaluation helpers for UI adapters

    /// <summary>
    /// Evaluates a component property for display. Errors are reported (once) and yield null;
    /// a value still waiting on an asynchronous call yields null with <paramref name="pending"/> set.
    /// </summary>
    public JsonNode? Evaluate(JsonNode? value, EvaluationContext context, out bool pending, EvaluationMode mode = EvaluationMode.Binding)
    {
        var result = Evaluate(value, context, out pending, out var errors, mode);
        foreach (var error in errors)
        {
            ReportOnce(error);
        }

        return result;
    }

    /// <summary>
    /// Evaluates a component property without reporting: <paramref name="errors"/> lists what went
    /// wrong (a failed evaluation yields null). <see cref="A2UIBinding"/> uses this to report each
    /// binding's errors once per change.
    /// </summary>
    public JsonNode? Evaluate(
        JsonNode? value, EvaluationContext context, out bool pending, out IReadOnlyList<A2UIException> errors, EvaluationMode mode = EvaluationMode.Binding) =>
        Evaluate(value, context, out pending, out errors, mode, requestedAgentCalls: null);

    internal JsonNode? Evaluate(
        JsonNode? value,
        EvaluationContext context,
        out bool pending,
        out IReadOnlyList<A2UIException> errors,
        EvaluationMode mode,
        ISet<string>? requestedAgentCalls)
    {
        var collected = new List<A2UIException>();
        errors = collected;
        var scope = new EvaluationScope(mode) { ReportError = collected.Add, RequestedAgentCalls = requestedAgentCalls };
        try
        {
            var result = Processor.Evaluator.Evaluate(value, context, scope, NotifyChanged);
            pending = scope.Pending;
            return result;
        }
        catch (A2UIException ex)
        {
            pending = false;
            if (ex.Code != A2UIErrorCodes.Cancelled)
            {
                collected.Add(ex);
            }

            return null;
        }
    }

    /// <summary>Evaluates a property and returns it as display text (null and missing values are empty).</summary>
    public string EvaluateString(JsonNode? value, EvaluationContext context) =>
        A2UIValues.ToDisplayString(Evaluate(value, context, out _));

    /// <summary>Evaluates a property as a <c>DynamicBoolean</c>.</summary>
    public bool EvaluateBoolean(JsonNode? value, EvaluationContext context) =>
        A2UIValues.ToBoolean(Evaluate(value, context, out _));

    /// <summary>Evaluates a property as a number, or null when it is not numeric.</summary>
    public double? EvaluateNumber(JsonNode? value, EvaluationContext context) =>
        A2UIValues.ToDouble(Evaluate(value, context, out _));

    /// <summary>
    /// Evaluates a component's <c>@checks</c> in declaration order. A check still waiting on
    /// the agent passes until it settles.
    /// </summary>
    public CheckOutcome EvaluateChecks(ComponentInstance component, EvaluationContext context)
    {
        if (component.Checks is not { Count: > 0 } checks)
        {
            return CheckOutcome.Valid;
        }

        var failures = new List<JsonObject>();
        foreach (var rule in checks.OfType<JsonObject>())
        {
            var message = A2UIValues.AsString(rule["message"]);
            var scope = new EvaluationScope(EvaluationMode.Check);
            JsonObject result;
            try
            {
                var value = Processor.Evaluator.Evaluate(rule["condition"], context, scope, NotifyChanged);
                if (scope.Pending)
                {
                    continue;
                }

                result = A2UIValues.ToValidationResult(value, message);
            }
            catch (A2UIException ex)
            {
                ReportOnce(ex);
                result = A2UIValues.ValidationResult(false, message ?? ex.Message, severity: "error");
            }

            if (result["valid"]?.GetValue<bool>() == false)
            {
                failures.Add(result);
            }
        }

        return failures.Count == 0 ? CheckOutcome.Valid : new CheckOutcome(failures);
    }

    /// <summary>
    /// Writes a user's input through a two-way binding (<c>{"@path": ...}</c>). Other values
    /// (literals or calls) are not writable and are ignored.
    /// </summary>
    public void Write(JsonNode? binding, EvaluationContext context, JsonNode? value)
    {
        if (binding is not JsonObject obj || A2UIValues.AsString(obj["@path"]) is not { } path || obj.Count != 1)
        {
            return;
        }

        try
        {
            context.Data.Set(context.Resolve(path), value);
        }
        catch (A2UIException ex)
        {
            ReportOnce(ex);
        }
    }

    /// <summary>
    /// Resolves the children of a container property: a static id list, a single id, or a
    /// template bound to a list in the data model (each item gets its own scope).
    /// </summary>
    public IReadOnlyList<(ComponentInstance Component, EvaluationContext Context)> ResolveChildren(
        ComponentInstance parent,
        string property,
        EvaluationContext context)
    {
        var result = new List<(ComponentInstance, EvaluationContext)>();
        var value = parent.Properties[property];
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var id):
                if (TryGetChild(parent, id, out var single))
                {
                    result.Add((single, context));
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is JsonValue iv && iv.TryGetValue<string>(out var childId) && TryGetChild(parent, childId, out var child))
                    {
                        result.Add((child, context));
                    }
                }

                break;
            case JsonObject template when A2UIValues.AsString(template["componentId"]) is { } templateId
                                          && A2UIValues.AsString(template["path"]) is { } listPath:
                if (!TryGetChild(parent, templateId, out var templateComponent))
                {
                    break;
                }

                JsonNode? list;
                try
                {
                    list = context.Data.Get(context.Resolve(listPath));
                }
                catch (A2UIException ex)
                {
                    ReportOnce(ex);
                    break;
                }

                if (list is JsonArray items)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        result.Add((templateComponent, context.ForTemplateItem(listPath, i)));
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>Looks up a child, applying the catalog's composition rules.</summary>
    public bool TryGetChild(ComponentInstance parent, string childId, out ComponentInstance child)
    {
        if (!_components.TryGetValue(childId, out child!))
        {
            return false;
        }

        return Composition.IsAllowed(parent.Definition, parent.Type, child.Definition, child.Type);
    }

    /// <summary>
    /// Runs a component's <c>action</c> after a user gesture: sends an <c>event</c> to the agent,
    /// or runs a local <c>functionCall</c>. Failures are reported and stop the action.
    /// </summary>
    public Task DispatchActionAsync(ComponentInstance component, EvaluationContext context, CancellationToken cancellationToken = default) =>
        Processor.DispatchActionAsync(this, component, component.Properties["action"], context, cancellationToken);

    internal void ReportOnce(A2UIException ex)
    {
        var key = $"{ex.Code}|{ex.Message}";
        if (_reportedErrors.Count < 256 && _reportedErrors.Add(key))
        {
            Processor.ReportError(ex, SurfaceId);
        }
    }

    internal JsonObject DataModelSnapshot() => DataModel.Root?.DeepClone() as JsonObject ?? new JsonObject();
}

/// <summary>The result of a component's <c>@checks</c>.</summary>
/// <param name="Failures">Failing <c>ValidationResult</c>s in declaration order, including warnings and info.</param>
public sealed record CheckOutcome(IReadOnlyList<JsonObject> Failures)
{
    public static CheckOutcome Valid { get; } = new([]);

    /// <summary>Failures that block (severity <c>error</c>).</summary>
    public IEnumerable<JsonObject> Errors => Failures.Where(f => A2UIValues.AsString(f["severity"]) is null or "error");

    /// <summary>True when no failure has severity <c>error</c>.</summary>
    public bool IsValid => !Errors.Any();

    /// <summary>Messages of the blocking failures.</summary>
    public IEnumerable<string> ErrorMessages => Errors.Select(f => A2UIValues.AsString(f["message"]) ?? "Invalid value.");
}

/// <summary>Catalog composition rules (<c>allowedParents</c> / <c>allowedChildren</c>).</summary>
public static class Composition
{
    /// <summary>The reserved type name of the surface container.</summary>
    public const string SurfaceType = "Surface";

    public static bool IsAllowed(ComponentDefinition? parent, string parentType, ComponentDefinition child, string childType)
    {
        if (parent?.AllowedChildren is { } allowedChildren && !allowedChildren.Contains(childType, StringComparer.Ordinal))
        {
            return false;
        }

        return child.AllowedParents is not { } allowedParents || allowedParents.Contains(parentType, StringComparer.Ordinal);
    }
}
