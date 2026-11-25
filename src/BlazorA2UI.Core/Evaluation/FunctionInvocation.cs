using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// The arguments and context handed to a function implementation.
/// </summary>
public sealed class FunctionInvocation
{
    /// <summary>
    /// Creates an invocation; the evaluator builds these.
    /// </summary>
    internal FunctionInvocation(
        string name,
        string? catalogId,
        JsonObject args,
        EvaluationContext context,
        EvaluationScope scope,
        A2UIEvaluator evaluator)
    {
        Name = name;
        CatalogId = catalogId;
        Args = args;
        Context = context;
        Scope = scope;
        Evaluator = evaluator;
    }

    /// <summary>
    /// Gets the function name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the id of the catalog the function was resolved in, or null for system functions.
    /// </summary>
    public string? CatalogId { get; }

    /// <summary>
    /// The arguments, with every binding and nested call already resolved.
    /// </summary>
    public JsonObject Args { get; }

    /// <summary>
    /// Gets the context the call is evaluated in.
    /// </summary>
    public EvaluationContext Context { get; }

    /// <summary>
    /// Gets the evaluation scope, with its mode, error sink and cancellation.
    /// </summary>
    public EvaluationScope Scope { get; }

    /// <summary>
    /// Gets the evaluator running the call, for functions that evaluate further values.
    /// </summary>
    public A2UIEvaluator Evaluator { get; }

    /// <summary>
    /// Host services (for example a URL launcher), when the processor was given a provider.
    /// </summary>
    public IServiceProvider? Services => Evaluator.Services;

    /// <summary>
    /// Gets the token that cancels the call.
    /// </summary>
    public CancellationToken CancellationToken => Scope.CancellationToken;

    /// <summary>
    /// Returns the resolved argument <paramref name="name"/>, or null when it is missing.
    /// </summary>
    public JsonNode? Arg(string name) => Args[name];
}
