using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>Routes renderer function calls that have no local implementation to the agent.</summary>
public interface IAgentFunctionRouter
{
    /// <summary>
    /// For bindings and checks: returns the settled answer of an identical call, or starts
    /// (or joins) one and reports <paramref name="pending"/>.
    /// </summary>
    /// <param name="catalogId">The catalog the call resolved to.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The resolved arguments.</param>
    /// <param name="requested">
    /// The calls the evaluating binding has already asked for. A failed call is sent again only for a
    /// binding that has not asked for it before; null means the caller keeps no such record, and a
    /// failure is then never re-sent (so a re-render cannot loop on a failing agent).
    /// </param>
    /// <param name="pending">Set while the agent has not answered.</param>
    JsonNode? GetOrStart(string catalogId, string name, JsonObject args, ISet<string>? requested, out bool pending);

    /// <summary>For actions: sends a fresh call and waits for the agent's answer.</summary>
    Task<JsonNode?> CallAsync(string catalogId, string name, JsonObject args, CancellationToken cancellationToken);
}
