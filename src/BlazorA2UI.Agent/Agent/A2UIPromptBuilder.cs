using System.Text;
using System.Text.Json;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Agent;

/// <summary>
/// Writes the part of a system prompt that teaches a model to answer with A2UI: the rules for A2UI JSON blocks, the
/// protocol and catalog schemas, the catalogs' instructions and optional examples.
/// </summary>
/// <remarks>
/// <para>
/// The schemas are the published v1.0 schemas, cut down to what the prompt needs. The agent-to-renderer schema keeps
/// only <see cref="AllowedMessages"/>; the common types keep only the definitions the catalogs and messages use; and
/// <c>agentOnly</c> functions are left out of the catalogs, since UI the model writes may never call them.
/// </para>
/// <para>
/// Read the answer with <see cref="A2UIResponseParser"/> or <see cref="A2UIResponseStream"/>, which understand the same
/// block format.
/// </para>
/// </remarks>
public sealed class A2UIPromptBuilder
{
    /// <summary>
    /// The line that opens the schema section.
    /// </summary>
    public const string SchemaBlockStart = "---BEGIN A2UI JSON SCHEMA---";

    /// <summary>
    /// The line that closes the schema section.
    /// </summary>
    public const string SchemaBlockEnd = "---END A2UI JSON SCHEMA---";

    /// <summary>
    /// Compact JSON with no escaping beyond what JSON needs, so the schemas read naturally and cost fewer tokens.
    /// </summary>
    private static readonly JsonSerializerOptions s_compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The catalogs, in the order the prompt lists them.
    /// </summary>
    private readonly List<Catalog> _catalogs = [];

    /// <summary>
    /// Creates a builder for the given catalogs.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalogs"/> or one of its catalogs is null.</exception>
    /// <exception cref="ArgumentException">There is no catalog, two share an id, or one is not for A2UI 1.0.</exception>
    /// <param name="catalogs">The catalogs the model may use, in the order the prompt lists them.</param>
    public A2UIPromptBuilder(IEnumerable<Catalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Catalog catalog in catalogs)
        {
            ArgumentNullException.ThrowIfNull(catalog, nameof(catalogs));
            if (!ids.Add(catalog.CatalogId))
            {
                throw new ArgumentException($"Two catalogs have the id '{catalog.CatalogId}'.", nameof(catalogs));
            }

            if (catalog.ProtocolVersion != A2UIProtocol.Version)
            {
                throw new ArgumentException($"Catalog '{catalog.CatalogId}' is for A2UI {catalog.ProtocolVersion}, not {A2UIProtocol.Version}.", nameof(catalogs));
            }

            _catalogs.Add(catalog);
        }

        if (_catalogs.Count == 0)
        {
            throw new ArgumentException("The prompt needs at least one catalog.", nameof(catalogs));
        }
    }

    /// <summary>
    /// Gets the messages the model may write. The default is <see cref="A2UIAgentMessages.Ui"/>.
    /// </summary>
    public A2UIAgentMessages AllowedMessages { get; init; } = A2UIAgentMessages.Ui;

    /// <summary>
    /// Gets the few-shot examples: each is the list of messages of one A2UI JSON block. They are checked with
    /// <see cref="A2UIMessageValidator"/> against the catalogs when the prompt is built.
    /// </summary>
    public IList<IReadOnlyList<JsonObject>> Examples { get; } = [];

    /// <summary>
    /// Gets the workflow rules the prompt starts with, which describe the block format the parsers read.
    /// </summary>
    public string Rules => BuildRules(AllowedMessages);

    /// <summary>
    /// Writes the prompt section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AllowedMessages"/> is empty, an example fails validation, or a catalog has a component that its
    /// document does not describe, so the model could not learn it.
    /// </exception>
    /// <returns>The text to add to the system prompt.</returns>
    public string Build()
    {
        if ((AllowedMessages & A2UIAgentMessages.All) == A2UIAgentMessages.None)
        {
            throw new InvalidOperationException("The prompt needs at least one allowed message.");
        }

        JsonObject agentToRenderer = SchemaDocuments.AgentToRenderer(AllowedMessages);
        List<JsonObject> catalogDocuments = [.. _catalogs.Select(CatalogDocument)];
        JsonObject? commonTypes = SchemaDocuments.CommonTypes([agentToRenderer, .. catalogDocuments]);

        StringBuilder prompt = new();
        prompt.Append(BuildRules(AllowedMessages)).Append('\n');
        prompt.Append('\n').Append(SchemaBlockStart).Append("\n\n");
        prompt.Append("### Agent to Renderer Schema:\n").Append(agentToRenderer.ToJsonString(s_compact)).Append("\n\n");
        if (commonTypes is not null)
        {
            prompt.Append("### Common Types Schema:\n").Append(commonTypes.ToJsonString(s_compact)).Append("\n\n");
        }

        foreach (JsonObject document in catalogDocuments)
        {
            prompt.Append("### Catalog Schema:\n").Append(document.ToJsonString(s_compact)).Append("\n\n");
        }

        prompt.Append(SchemaBlockEnd).Append('\n');
        foreach (Catalog catalog in _catalogs)
        {
            if (!string.IsNullOrWhiteSpace(catalog.Instructions))
            {
                prompt.Append("\n### Instructions for catalog ").Append(catalog.CatalogId).Append(":\n").Append(catalog.Instructions.Trim()).Append('\n');
            }
        }

        if (Examples.Count > 0)
        {
            prompt.Append("\n### Examples:\n");
            for (int i = 0; i < Examples.Count; i++)
            {
                prompt.Append('\n').Append(ExampleBlock(i)).Append('\n');
            }
        }

        return prompt.ToString();
    }

    /// <summary>
    /// Writes the workflow rules for a set of allowed messages.
    /// </summary>
    /// <param name="allowed">The messages the model may write.</param>
    private static string BuildRules(A2UIAgentMessages allowed)
    {
        List<string> names = [];
        foreach ((A2UIAgentMessages message, string key, string _) in AgentMessageNames.s_all)
        {
            if ((allowed & message) != 0)
            {
                names.Add(key);
            }
        }

        return $"""
            Your response can contain A2UI JSON blocks, which the client renders as a user interface. Follow these rules:
            - Wrap each A2UI JSON block in literal {A2UIResponseParser.OpenTag} and {A2UIResponseParser.CloseTag} tags. Always write both tags. Do not wrap the tags in backticks, and do not replace them with a Markdown code fence such as ```json.
            - You can write conversational text before, between and after the blocks.
            - The content of each block MUST be a JSON array of A2UI messages. Each message MUST follow the A2UI JSON schema below and include "version": "{A2UIProtocol.Version}".
            - Use only these messages: {string.Join(", ", names)}.
            - Use only the components and functions that the catalog schemas below define.
            """;
    }

    /// <summary>
    /// Returns the schema document of a catalog as the prompt shows it: its own document, with the functions it
    /// declares in code added and its <c>agentOnly</c> functions left out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The catalog has a component its document does not describe.</exception>
    /// <param name="catalog">The catalog.</param>
    private static JsonObject CatalogDocument(Catalog catalog)
    {
        JsonObject document = catalog.Document?.DeepClone().AsObject() ?? new JsonObject();
        document["catalogId"] = catalog.CatalogId;
        document["protocolVersion"] = catalog.ProtocolVersion;
        JsonObject components = document["components"] as JsonObject ?? new JsonObject();
        foreach (string name in catalog.Components.Keys)
        {
            if (!components.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Catalog '{catalog.CatalogId}' has a component '{name}' that its document does not describe, so the prompt cannot teach it. Load the catalog from a document that describes it.");
            }
        }

        document["components"] = components;
        JsonObject functions = document["functions"] as JsonObject ?? new JsonObject();
        foreach (string name in functions.Where(p => p.Value?["allowedCallers"] is JsonValue callers && A2UIValues.AsString(callers) == "agentOnly").Select(p => p.Key).ToList())
        {
            functions.Remove(name);
        }

        foreach (FunctionDefinition function in catalog.Functions.Values)
        {
            if (function.AllowedCallers == AllowedCallers.AgentOnly)
            {
                functions.Remove(function.Name);
            }
            else if (!functions.ContainsKey(function.Name))
            {
                functions[function.Name] = FunctionSchema(function);
            }
        }

        if (functions.Count > 0)
        {
            document["functions"] = functions;
        }
        else
        {
            document.Remove("functions");
        }

        return document;
    }

    /// <summary>
    /// Writes the catalog schema of a function declared in code, as a catalog document would declare it.
    /// </summary>
    /// <param name="function">The function.</param>
    private static JsonObject FunctionSchema(FunctionDefinition function)
    {
        JsonObject properties = new() { ["@call"] = new JsonObject { ["const"] = function.Name } };
        if (function.ArgsSchema is { } args)
        {
            properties["args"] = args.DeepClone();
        }

        JsonObject schema = new() { ["type"] = "object" };
        if (function.Description is { } description)
        {
            schema["description"] = description;
        }

        schema["properties"] = properties;
        schema["required"] = new JsonArray("@call");
        schema["returnType"] = function.ReturnType;
        schema["allowedCallers"] = function.AllowedCallers switch
        {
            AllowedCallers.RendererOrAgent => "rendererOrAgent",
            _ => "rendererOnly",
        };
        if (function.RequiresUserActivation)
        {
            schema["requiresUserActivation"] = true;
        }

        return schema;
    }

    /// <summary>
    /// Checks an example against the catalogs and writes it as an A2UI JSON block.
    /// </summary>
    /// <exception cref="InvalidOperationException">The example has a message the prompt does not allow, or a renderer would reject it.</exception>
    /// <param name="index">The example's index.</param>
    private string ExampleBlock(int index)
    {
        IReadOnlyList<JsonObject> messages = Examples[index] ?? throw new InvalidOperationException($"Example {index + 1} is null.");
        using A2UIMessageValidator validator = new(_catalogs);
        JsonArray block = [];
        foreach (JsonObject message in messages)
        {
            string key = message.Select(p => p.Key).FirstOrDefault(k => k != "version") ?? string.Empty;
            if ((AgentMessageNames.FromKey(key) & AllowedMessages) == A2UIAgentMessages.None)
            {
                throw new InvalidOperationException($"Example {index + 1} has a '{key}' message, which the prompt does not allow.");
            }

            A2UIValidationResult result = validator.Validate(message);
            if (!result.IsValid)
            {
                A2UIErrorReport first = result.Errors[0];
                throw new InvalidOperationException($"Example {index + 1} would be rejected by the renderer: {first.Code}: {first.Message}");
            }

            block.Add(message.DeepClone());
        }

        return $"{A2UIResponseParser.OpenTag}\n{block.ToJsonString(s_compact)}\n{A2UIResponseParser.CloseTag}";
    }
}
