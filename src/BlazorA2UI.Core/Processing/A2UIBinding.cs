using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.Processing;

/// <summary>
/// One evaluated expression of a rendered component (a property, an accessibility attribute or a
/// check rule). Call <see cref="Refresh"/> when the surface changes. Errors are reported to the
/// surface when they first appear for this binding, not on every refresh, so a re-render cannot
/// flood the agent with the same error.
/// </summary>
public sealed class A2UIBinding
{
    /// <summary>
    /// The agent calls this binding has asked for, so a failed call is not re-sent for it on every refresh.
    /// </summary>
    private readonly HashSet<string> _requestedAgentCalls = new(StringComparer.Ordinal);
    /// <summary>
    /// The errors reported for the current evaluation, so each is reported once until it goes away.
    /// </summary>
    private HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a binding; call <see cref="Refresh"/> to evaluate it.
    /// </summary>
    /// <param name="surface">The surface the expression belongs to.</param>
    /// <param name="expression">The expression: a literal, an <c>@path</c> binding or an <c>@call</c>.</param>
    /// <param name="context">The data context to evaluate in.</param>
    /// <param name="mode">What the evaluation is for.</param>
    public A2UIBinding(Surface surface, JsonNode? expression, EvaluationContext context, EvaluationMode mode = EvaluationMode.Binding)
    {
        Surface = surface;
        Expression = expression;
        Context = context;
        Mode = mode;
    }

    /// <summary>
    /// Gets the surface the expression belongs to.
    /// </summary>
    public Surface Surface { get; }

    /// <summary>
    /// Gets the expression as the agent sent it.
    /// </summary>
    public JsonNode? Expression { get; }

    /// <summary>
    /// Gets the data context the expression is evaluated in.
    /// </summary>
    public EvaluationContext Context { get; }

    /// <summary>
    /// Gets what the evaluation is for.
    /// </summary>
    public EvaluationMode Mode { get; }

    /// <summary>
    /// The last evaluated value; null while pending or after an error.
    /// </summary>
    public JsonNode? Value { get; private set; }

    /// <summary>
    /// True while part of the value waits on an asynchronous (agent) call.
    /// </summary>
    public bool IsPending { get; private set; }

    /// <summary>
    /// The errors of the last evaluation.
    /// </summary>
    public IReadOnlyList<A2UIException> Errors { get; private set; } = [];

    /// <summary>
    /// Evaluates the expression again and reports errors that are new for this binding.
    /// </summary>
    public JsonNode? Refresh()
    {
        Value = Surface.Evaluate(Expression, Context, out bool pending, out IReadOnlyList<A2UIException>? errors, Mode, _requestedAgentCalls);
        IsPending = pending;
        Errors = errors;
        HashSet<string> current = new(StringComparer.Ordinal);
        foreach (A2UIException error in errors)
        {
            string key = $"{error.Code}|{error.Message}";
            current.Add(key);
            if (!_reported.Contains(key) && !Surface.IsDeleted)
            {
                Surface.Processor.ReportError(error, Surface.SurfaceId);
            }
        }

        _reported = current;
        return Value;
    }
}
