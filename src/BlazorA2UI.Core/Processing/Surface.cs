using System.Diagnostics.CodeAnalysis;
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
    /// <summary>
    /// The surface's components, by id.
    /// </summary>
    private readonly Dictionary<string, ComponentInstance> _components = new(StringComparer.Ordinal);
    /// <summary>
    /// The errors already reported for this surface's components, so a re-render does not report them again.
    /// </summary>
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    /// <summary>
    /// How many <see cref="BatchChanges"/> scopes are open.
    /// </summary>
    private int _changeSuspension;
    /// <summary>
    /// Whether a change happened while changes were batched.
    /// </summary>
    private bool _changePending;

    /// <summary>
    /// Creates a surface; <see cref="MessageProcessor"/> builds these for <c>createSurface</c>.
    /// </summary>
    internal Surface(MessageProcessor processor, string surfaceId, string? catalogId, bool sendDataModel, JsonObject? metadata)
    {
        Processor = processor;
        SurfaceId = surfaceId;
        CatalogId = catalogId;
        SendDataModel = sendDataModel;
        Metadata = metadata;
        // Only catalogs for this protocol version are available to the surface.
        CatalogIds = processor.Catalogs.Catalogs
            .Where(c => c.ProtocolVersion == A2UIProtocol.Version)
            .Select(c => c.CatalogId)
            .ToHashSet(StringComparer.Ordinal);
        DataModel = new DataModel();
        DataModel.Changed += _ => NotifyChanged();
        AgentCalls = new AgentCallManager(this);
    }

    /// <summary>
    /// Gets the processor that owns the surface.
    /// </summary>
    public MessageProcessor Processor { get; }

    /// <summary>
    /// The catalogs this surface can use: those registered when it was created.
    /// </summary>
    public IReadOnlySet<string> CatalogIds { get; }

    /// <summary>
    /// Looks up a catalog this surface can use.
    /// </summary>
    internal bool TryGetCatalog(string catalogId, [MaybeNullWhen(false)] out Catalogs.Catalog catalog)
    {
        catalog = null;
        return CatalogIds.Contains(catalogId) && Processor.Catalogs.TryGet(catalogId, out catalog);
    }

    /// <summary>
    /// Gets the surface id.
    /// </summary>
    public string SurfaceId { get; }

    /// <summary>
    /// The surface's default catalog id, from <c>createSurface</c>.
    /// </summary>
    public string? CatalogId { get; }

    /// <summary>
    /// Gets whether the agent asked for the data model to be sent with every event (<c>sendDataModel</c>).
    /// </summary>
    public bool SendDataModel { get; }

    /// <summary>
    /// Surface metadata (<c>metadata.extensions</c>) from <c>createSurface</c>.
    /// </summary>
    public JsonObject? Metadata { get; }

    /// <summary>
    /// Gets the surface's data model.
    /// </summary>
    public DataModel DataModel { get; }

    /// <summary>
    /// Gets the surface's components, by id.
    /// </summary>
    public IReadOnlyDictionary<string, ComponentInstance> Components => _components;

    /// <summary>
    /// The root component, once the agent has sent one with id <c>root</c>.
    /// </summary>
    public ComponentInstance? Root => _components.GetValueOrDefault("root");

    /// <summary>
    /// Gets whether the agent deleted the surface with <c>deleteSurface</c>.
    /// </summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>
    /// Gets the calls this surface has routed to the agent.
    /// </summary>
    internal AgentCallManager AgentCalls { get; }

    /// <summary>
    /// Gets the asynchronous local calls that the surface's bindings and checks started, kept until their inputs change.
    /// </summary>
    internal LocalCallCache LocalCalls { get; } = new();

    /// <summary>
    /// Routes calls without a local implementation to the agent.
    /// </summary>
    public IAgentFunctionRouter AgentRouter => AgentCalls;

    /// <summary>
    /// Raised when components or data change, or an asynchronous value settles.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The root evaluation context of this surface.
    /// </summary>
    public EvaluationContext RootContext => EvaluationContext.ForSurface(this);

    /// <summary>
    /// Adds or replaces components, clears the reported errors, and raises <see cref="Changed"/>.
    /// </summary>
    internal void ReplaceComponents(IEnumerable<ComponentInstance> components)
    {
        foreach (ComponentInstance component in components)
        {
            _components[component.Id] = component;
        }

        _reportedErrors.Clear();
        NotifyChanged();
    }

    /// <summary>
    /// Returns a copy of the component map, for validating an update before it is applied.
    /// </summary>
    internal Dictionary<string, ComponentInstance> SnapshotComponents() => new(_components, StringComparer.Ordinal);

    /// <summary>
    /// Puts back a component map taken with <see cref="SnapshotComponents"/>, dropping the components added since,
    /// clears the reported errors, and raises <see cref="Changed"/>.
    /// </summary>
    /// <param name="components">The component map to restore.</param>
    internal void RestoreComponents(Dictionary<string, ComponentInstance> components)
    {
        _components.Clear();
        foreach ((string id, ComponentInstance component) in components)
        {
            _components[id] = component;
        }

        _reportedErrors.Clear();
        NotifyChanged();
    }

    /// <summary>
    /// Raises <see cref="Changed"/> (coalesced while updates are batched).
    /// </summary>
    public void NotifyChanged()
    {
        if (_changeSuspension > 0)
        {
            _changePending = true;
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Holds back <see cref="Changed"/> until the returned scope is disposed, then raises it once if anything changed.
    /// </summary>
    internal IDisposable BatchChanges()
    {
        _changeSuspension++;
        return new Resume(this);
    }

    /// <summary>
    /// Ends a <see cref="BatchChanges"/> scope.
    /// </summary>
    /// <param name="surface">The surface whose changes are batched.</param>
    private sealed class Resume(Surface surface) : IDisposable
    {
        /// <summary>
        /// Whether this scope has already ended.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Ends the scope; disposing twice does nothing.
        /// </summary>
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
        JsonNode? result = Evaluate(value, context, out pending, out IReadOnlyList<A2UIException>? errors, mode);
        foreach (A2UIException error in errors)
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

    /// <summary>
    /// Evaluates a value with the given set of already-requested agent calls; <see cref="A2UIBinding"/> passes its own set.
    /// </summary>
    internal JsonNode? Evaluate(
        JsonNode? value,
        EvaluationContext context,
        out bool pending,
        out IReadOnlyList<A2UIException> errors,
        EvaluationMode mode,
        ISet<string>? requestedAgentCalls)
    {
        List<A2UIException> collected = new();
        errors = collected;
        EvaluationScope scope = new(mode) { ReportError = collected.Add, RequestedAgentCalls = requestedAgentCalls };
        try
        {
            JsonNode? result = Processor.Evaluator.Evaluate(value, context, scope, NotifyChanged);
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

    /// <summary>
    /// Evaluates a property and returns it as display text (null and missing values are empty).
    /// </summary>
    public string EvaluateString(JsonNode? value, EvaluationContext context) =>
        A2UIValues.ToDisplayString(Evaluate(value, context, out _));

    /// <summary>
    /// Evaluates a property as a <c>DynamicBoolean</c>.
    /// </summary>
    public bool EvaluateBoolean(JsonNode? value, EvaluationContext context) =>
        A2UIValues.ToBoolean(Evaluate(value, context, out _));

    /// <summary>
    /// Evaluates a property as a number, or null when it is not numeric.
    /// </summary>
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

        List<JsonObject> failures = new();
        foreach (JsonObject rule in checks.OfType<JsonObject>())
        {
            string? message = A2UIValues.AsString(rule["message"]);
            EvaluationScope scope = new(EvaluationMode.Check);
            JsonObject result;
            try
            {
                JsonNode? value = Processor.Evaluator.Evaluate(rule["condition"], context, scope, NotifyChanged);
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
        List<(ComponentInstance, EvaluationContext)> result = new();
        JsonNode? value = parent.Properties[property];
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out string? id):
                if (TryGetChild(parent, id, out ComponentInstance? single))
                {
                    result.Add((single, context));
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    if (item is JsonValue iv && iv.TryGetValue<string>(out string? childId) && TryGetChild(parent, childId, out ComponentInstance? child))
                    {
                        result.Add((child, context));
                    }
                }

                break;
            case JsonObject template when A2UIValues.AsString(template["componentId"]) is { } templateId
                                          && A2UIValues.AsString(template["path"]) is { } listPath:
                if (!TryGetChild(parent, templateId, out ComponentInstance? templateComponent))
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
                    for (int i = 0; i < items.Count; i++)
                    {
                        result.Add((templateComponent, context.ForTemplateItem(listPath, i)));
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>
    /// Looks up a child, applying the catalog's composition rules.
    /// </summary>
    /// <returns>False when no component has the id, or its catalog does not allow it under <paramref name="parent"/>.</returns>
    public bool TryGetChild(ComponentInstance parent, string childId, [MaybeNullWhen(false)] out ComponentInstance child)
    {
        if (!_components.TryGetValue(childId, out child))
        {
            return false;
        }

        return Composition.IsAllowed(parent.Definition, parent.Type, child.Definition, child.Type);
    }

    /// <summary>
    /// Runs a component's <c>action</c> after a user gesture: sends an <c>event</c> to the agent,
    /// or runs a local <c>functionCall</c>. Failures are reported and stop the action.
    /// </summary>
    /// <returns>True when the action ran; false when the component has no action or the action failed and was reported.</returns>
    public Task<bool> DispatchActionAsync(ComponentInstance component, EvaluationContext context, CancellationToken cancellationToken = default) =>
        Processor.DispatchActionAsync(this, component, component.Properties["action"], context, metadata: null, cancellationToken);

    /// <summary>
    /// Runs a component's <c>action</c> after a user gesture, attaching the host's <paramref name="metadata"/>
    /// (<c>{"extensions": {...}}</c>) to a dispatched event. Failures, including invalid metadata, are reported and stop the action.
    /// </summary>
    /// <returns>True when the action ran; false when the component has no action or the action failed and was reported.</returns>
    public Task<bool> DispatchActionAsync(ComponentInstance component, EvaluationContext context, JsonObject? metadata, CancellationToken cancellationToken = default) =>
        Processor.DispatchActionAsync(this, component, component.Properties["action"], context, metadata, cancellationToken);

    /// <summary>
    /// Reports an error unless the same code and message were already reported for this surface. At most 256 distinct errors are reported.
    /// </summary>
    internal void ReportOnce(A2UIException ex)
    {
        string key = $"{ex.Code}|{ex.Message}";
        if (_reportedErrors.Count < 256 && _reportedErrors.Add(key))
        {
            Processor.ReportError(ex, SurfaceId);
        }
    }

    /// <summary>
    /// Returns a copy of the data model's root, whatever its kind, or an empty object when nothing was written.
    /// </summary>
    internal JsonNode DataModelSnapshot() => DataModel.Root?.DeepClone() ?? new JsonObject();
}

/// <summary>
/// The result of a component's <c>@checks</c>.
/// </summary>
/// <param name="Failures">Failing <c>ValidationResult</c>s in declaration order, including warnings and info.</param>
public sealed record CheckOutcome(IReadOnlyList<JsonObject> Failures)
{
    /// <summary>
    /// Gets the outcome with no failures.
    /// </summary>
    public static CheckOutcome Valid { get; } = new([]);

    /// <summary>
    /// Failures that block (severity <c>error</c>).
    /// </summary>
    public IEnumerable<JsonObject> Errors => Failures.Where(f => A2UIValues.AsString(f["severity"]) is null or "error");

    /// <summary>
    /// True when no failure has severity <c>error</c>.
    /// </summary>
    public bool IsValid => !Errors.Any();

    /// <summary>
    /// Messages of the blocking failures.
    /// </summary>
    public IEnumerable<string> ErrorMessages => Errors.Select(f => A2UIValues.AsString(f["message"]) ?? "Invalid value.");
}

/// <summary>
/// Catalog composition rules (<c>allowedParents</c> / <c>allowedChildren</c>).
/// </summary>
public static class Composition
{
    /// <summary>
    /// The reserved type name of the surface container.
    /// </summary>
    public const string SurfaceType = "Surface";

    /// <summary>
    /// Whether <paramref name="child"/> may be placed under <paramref name="parent"/> by both components' composition rules.
    /// </summary>
    /// <param name="parent">The parent's definition, or null for the surface itself.</param>
    /// <param name="parentType">The parent's type name.</param>
    /// <param name="child">The child's definition.</param>
    /// <param name="childType">The child's type name.</param>
    public static bool IsAllowed(ComponentDefinition? parent, string parentType, ComponentDefinition child, string childType)
    {
        if (parent?.AllowedChildren is { } allowedChildren && !allowedChildren.Contains(childType, StringComparer.Ordinal))
        {
            return false;
        }

        return child.AllowedParents is not { } allowedParents || allowedParents.Contains(parentType, StringComparer.Ordinal);
    }
}
