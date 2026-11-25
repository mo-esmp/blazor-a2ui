namespace BlazorA2UI.Evaluation;

/// <summary>
/// Why an expression is being evaluated, which decides what it may call.
/// </summary>
public enum EvaluationMode
{
    /// <summary>
    /// A component property binding (reactive, no user activation).
    /// </summary>
    Binding,

    /// <summary>
    /// A <c>CheckRule.condition</c> (reactive, no user activation).
    /// </summary>
    Check,

    /// <summary>
    /// An action triggered by a user gesture (imperative, user-activated).
    /// </summary>
    Action,

    /// <summary>
    /// An agent-initiated <c>callRendererFunction</c>.
    /// </summary>
    AgentCall,
}

/// <summary>
/// Per-evaluation settings and results.
/// </summary>
public sealed class EvaluationScope
{
    /// <summary>
    /// Creates a scope for one evaluation in <paramref name="mode"/>.
    /// </summary>
    public EvaluationScope(EvaluationMode mode)
    {
        Mode = mode;
    }

    /// <summary>
    /// Gets what the evaluation is for, which decides the call boundaries it enforces.
    /// </summary>
    public EvaluationMode Mode { get; }

    /// <summary>
    /// True when the evaluation runs from a user gesture: by default for actions only, unless
    /// <see cref="UserActivationOverride"/> says otherwise.
    /// </summary>
    public bool UserActivated => UserActivationOverride ?? Mode == EvaluationMode.Action;

    /// <summary>
    /// Gets whether a user gesture is present, when the host knows better than the mode: an action the
    /// host runs without a gesture, or an agent call the host processes inside one. Null uses the mode.
    /// </summary>
    internal bool? UserActivationOverride { get; init; }

    /// <summary>
    /// True for bindings and checks, whose remote calls are shared and cached.
    /// </summary>
    public bool IsReactive => Mode is EvaluationMode.Binding or EvaluationMode.Check;

    /// <summary>
    /// The catalog that calls without a <c>catalogId</c> resolve against inside
    /// <c>formatString</c> templates (the catalog the template's own call resolved to).
    /// </summary>
    public string? CatalogOverride { get; internal set; }

    /// <summary>
    /// Set when some part of the value waits on an asynchronous call.
    /// </summary>
    public bool Pending { get; internal set; }

    /// <summary>
    /// True while evaluating the parts of a <c>formatString</c> template, which never route to the agent.
    /// </summary>
    public bool InTemplate { get; internal set; }

    /// <summary>
    /// Receives errors that do not stop the evaluation, such as a <c>formatString</c> template
    /// part that fails and renders empty. When null, such errors are thrown instead.
    /// </summary>
    public Action<A2UIException>? ReportError { get; init; }

    /// <summary>
    /// The agent calls the evaluating binding has asked for, kept across its evaluations (see
    /// <see cref="IAgentFunctionRouter.GetOrStart"/>).
    /// </summary>
    public ISet<string>? RequestedAgentCalls { get; init; }

    /// <summary>
    /// Gets or sets the current call nesting depth, used to stop runaway recursion.
    /// </summary>
    internal int Depth { get; set; }

    /// <summary>
    /// Gets the token that cancels the evaluation and any agent call it starts.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}
