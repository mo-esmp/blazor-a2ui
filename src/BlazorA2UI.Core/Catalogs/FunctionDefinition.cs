using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.Catalogs;

/// <summary>Who may invoke a catalog function.</summary>
public enum AllowedCallers
{
    /// <summary>Only the renderer (bindings, checks, actions). The default.</summary>
    RendererOnly,

    /// <summary>Only the agent, through <c>callRendererFunction</c>.</summary>
    AgentOnly,

    /// <summary>Both the renderer and the agent.</summary>
    RendererOrAgent,
}

/// <summary>A function implementation that runs on the renderer.</summary>
/// <param name="invocation">The resolved arguments and evaluation context.</param>
public delegate ValueTask<JsonNode?> A2UIFunction(FunctionInvocation invocation);

/// <summary>A catalog function: its declaration and, optionally, a renderer-side implementation.</summary>
public sealed class FunctionDefinition
{
    public FunctionDefinition(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary><c>string</c>, <c>number</c>, <c>boolean</c>, <c>array</c>, <c>object</c>, <c>validationResult</c>, <c>any</c> or <c>void</c>.</summary>
    public string ReturnType { get; init; } = "any";

    public AllowedCallers AllowedCallers { get; init; } = AllowedCallers.RendererOnly;

    /// <summary>When true the function only runs from a user gesture (an action), never from a binding or check.</summary>
    public bool RequiresUserActivation { get; init; }

    /// <summary>The JSON Schema of the function's <c>args</c> object, when declared.</summary>
    public JsonObject? ArgsSchema { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// The renderer-side implementation. A declared function without one is
    /// schema-only: renderer calls to it are routed to the agent.
    /// </summary>
    public A2UIFunction? Implementation { get; init; }

    public bool IsLocal => Implementation is not null;

    public FunctionDefinition WithImplementation(A2UIFunction implementation) => new(Name)
    {
        ReturnType = ReturnType,
        AllowedCallers = AllowedCallers,
        RequiresUserActivation = RequiresUserActivation,
        ArgsSchema = ArgsSchema,
        Description = Description,
        Implementation = implementation,
    };

    internal static AllowedCallers ParseCallers(string? value) => value switch
    {
        null or "rendererOnly" => AllowedCallers.RendererOnly,
        "agentOnly" => AllowedCallers.AgentOnly,
        "rendererOrAgent" => AllowedCallers.RendererOrAgent,
        _ => throw new A2UICatalogException($"Unknown allowedCallers value '{value}'."),
    };
}
