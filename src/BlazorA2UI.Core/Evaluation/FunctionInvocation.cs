using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>The arguments and context handed to a function implementation.</summary>
public sealed class FunctionInvocation
{
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

    public string Name { get; }

    public string? CatalogId { get; }

    /// <summary>The arguments, with every binding and nested call already resolved.</summary>
    public JsonObject Args { get; }

    public EvaluationContext Context { get; }

    public EvaluationScope Scope { get; }

    public A2UIEvaluator Evaluator { get; }

    /// <summary>Host services (for example a URL launcher), when the processor was given a provider.</summary>
    public IServiceProvider? Services => Evaluator.Services;

    public CancellationToken CancellationToken => Scope.CancellationToken;

    public JsonNode? Arg(string name) => Args[name];
}
