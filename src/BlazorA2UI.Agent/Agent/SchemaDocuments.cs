using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.Agent;

/// <summary>
/// The A2UI v1.0 protocol schemas a prompt shows the model, and the pruning that keeps only what it needs.
/// </summary>
internal static class SchemaDocuments
{
    /// <summary>
    /// The prefix of a reference to a definition in the same document.
    /// </summary>
    private const string LocalRefPrefix = "#/$defs/";

    /// <summary>
    /// The prefix of a reference to a definition in <c>common_types.json</c>.
    /// </summary>
    private const string CommonRefPrefix = "common_types.json#/$defs/";

    /// <summary>
    /// The embedded <c>agent_to_renderer.json</c>, parsed once.
    /// </summary>
    private static readonly Lazy<JsonObject> s_agentToRenderer =
        new(() => Load(typeof(SchemaDocuments).Assembly, "BlazorA2UI.Agent.Schemas.agent_to_renderer.json"));

    /// <summary>
    /// The core's embedded <c>common_types.json</c>, parsed once.
    /// </summary>
    private static readonly Lazy<JsonObject> s_commonTypes =
        new(() => Load(typeof(Catalog).Assembly, "BlazorA2UI.Schemas.common_types.json"));

    /// <summary>
    /// Returns a copy of the agent-to-renderer schema that admits only the allowed messages, without the definitions
    /// only the removed messages use.
    /// </summary>
    /// <param name="allowed">The messages to keep.</param>
    public static JsonObject AgentToRenderer(A2UIAgentMessages allowed)
    {
        JsonObject schema = (JsonObject)s_agentToRenderer.Value.DeepClone();
        HashSet<string> kept = new(StringComparer.Ordinal);
        foreach ((A2UIAgentMessages message, string _, string definition) in AgentMessageNames.s_all)
        {
            if ((allowed & message) != 0)
            {
                kept.Add(definition);
            }
        }

        JsonArray oneOf = [];
        foreach (JsonNode? option in (JsonArray)schema["oneOf"]!)
        {
            // Every option of the published schema is a local reference to a message definition.
            string target = A2UIValues.AsString(option!["$ref"])!.Substring(LocalRefPrefix.Length);
            if (kept.Contains(target))
            {
                oneOf.Add(option.DeepClone());
            }
        }

        schema["oneOf"] = oneOf;
        KeepReachable((JsonObject)schema["$defs"]!, oneOf, LocalRefPrefix);
        return schema;
    }

    /// <summary>
    /// Returns a copy of the common types schema with only the definitions that the given documents reference,
    /// directly or through other common types, or null when they reference none.
    /// </summary>
    /// <param name="documents">The documents shown with it: the agent-to-renderer schema and the catalogs.</param>
    public static JsonObject? CommonTypes(IEnumerable<JsonNode> documents)
    {
        HashSet<string> roots = new(StringComparer.Ordinal);
        foreach (JsonNode document in documents)
        {
            CollectRefs(document, CommonRefPrefix, roots, 0);
        }

        if (roots.Count == 0)
        {
            return null;
        }

        JsonObject schema = (JsonObject)s_commonTypes.Value.DeepClone();
        JsonObject defs = (JsonObject)schema["$defs"]!;
        HashSet<string> reachable = Closure(defs, roots, LocalRefPrefix);
        foreach (string name in defs.Select(p => p.Key).ToList())
        {
            if (!reachable.Contains(name))
            {
                defs.Remove(name);
            }
        }

        return schema;
    }

    /// <summary>
    /// Removes the definitions that nothing reachable from <paramref name="roots"/> references.
    /// </summary>
    /// <param name="defs">The definitions, changed in place.</param>
    /// <param name="roots">The schema parts that are kept.</param>
    /// <param name="prefix">The reference prefix of a definition in <paramref name="defs"/>.</param>
    private static void KeepReachable(JsonObject defs, JsonNode roots, string prefix)
    {
        HashSet<string> start = new(StringComparer.Ordinal);
        CollectRefs(roots, prefix, start, 0);
        HashSet<string> reachable = Closure(defs, start, prefix);
        foreach (string name in defs.Select(p => p.Key).ToList())
        {
            if (!reachable.Contains(name))
            {
                defs.Remove(name);
            }
        }
    }

    /// <summary>
    /// Returns the definitions reachable from a set of names, following references between definitions.
    /// </summary>
    /// <param name="defs">The definitions.</param>
    /// <param name="start">The names to start from.</param>
    /// <param name="prefix">The reference prefix of a definition in <paramref name="defs"/>.</param>
    private static HashSet<string> Closure(JsonObject defs, HashSet<string> start, string prefix)
    {
        HashSet<string> reachable = new(StringComparer.Ordinal);
        Queue<string> queue = new(start);
        while (queue.TryDequeue(out string? name))
        {
            if (!reachable.Add(name) || defs[name] is not { } definition)
            {
                continue;
            }

            HashSet<string> found = new(StringComparer.Ordinal);
            CollectRefs(definition, prefix, found, 0);
            foreach (string next in found)
            {
                queue.Enqueue(next);
            }
        }

        return reachable;
    }

    /// <summary>
    /// Collects the names of the definitions that <c>$ref</c> values with a prefix point to.
    /// </summary>
    /// <param name="node">The schema part to search.</param>
    /// <param name="prefix">The reference prefix.</param>
    /// <param name="names">Receives the names.</param>
    /// <param name="depth">The nesting depth; schemas deeper than 128 levels are not searched further.</param>
    private static void CollectRefs(JsonNode? node, string prefix, HashSet<string> names, int depth)
    {
        if (depth > 128)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (key == "$ref" && A2UIValues.AsString(value) is { } target && target.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        names.Add(target[prefix.Length..]);
                    }
                    else
                    {
                        CollectRefs(value, prefix, names, depth + 1);
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    CollectRefs(item, prefix, names, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Reads an embedded JSON schema.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resource is missing.</exception>
    /// <param name="assembly">The assembly that embeds it.</param>
    /// <param name="name">The resource name.</param>
    private static JsonObject Load(System.Reflection.Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded schema '{name}' is missing.");
        return (JsonObject)JsonNode.Parse(stream)!;
    }
}
