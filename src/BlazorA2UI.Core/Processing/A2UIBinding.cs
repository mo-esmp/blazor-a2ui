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
    private readonly HashSet<string> _requestedAgentCalls = new(StringComparer.Ordinal);
    private HashSet<string> _reported = new(StringComparer.Ordinal);

    public A2UIBinding(Surface surface, JsonNode? expression, EvaluationContext context, EvaluationMode mode = EvaluationMode.Binding)
    {
        Surface = surface;
        Expression = expression;
        Context = context;
        Mode = mode;
    }

    public Surface Surface { get; }

    public JsonNode? Expression { get; }

    public EvaluationContext Context { get; }

    public EvaluationMode Mode { get; }

    /// <summary>The last evaluated value; null while pending or after an error.</summary>
    public JsonNode? Value { get; private set; }

    /// <summary>True while part of the value waits on an asynchronous (agent) call.</summary>
    public bool IsPending { get; private set; }

    /// <summary>The errors of the last evaluation.</summary>
    public IReadOnlyList<A2UIException> Errors { get; private set; } = [];

    /// <summary>Evaluates the expression again and reports errors that are new for this binding.</summary>
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
