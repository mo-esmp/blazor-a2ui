using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Text;

namespace BlazorA2UI.Catalogs;

/// <summary>
/// An A2UI catalog: the component types and functions a surface may use, identified by
/// <see cref="CatalogId"/>. Load one from its JSON Schema document with <see cref="FromJson(string)"/>
/// and attach renderer-side function implementations with <see cref="WithFunctions"/>.
/// </summary>
public sealed class Catalog
{
    private static readonly HashSet<string> s_allowedRootKeys =
    [
        "$schema", "$id", "protocolVersion", "title", "description", "catalogId", "instructions",
        "metadata", "components", "functions", "$defs",
    ];

    private static readonly HashSet<string> s_allowedCommonRefs =
    [
        "ComponentId", "Child", "ChildList", "DynamicString", "DynamicNumber", "DynamicBoolean",
        "DynamicStringList", "DynamicValue", "AccessibilityAttributes", "CheckRule", "Checkable", "Action",
        "DataBinding", "FunctionCall",
    ];

    public Catalog(
        string catalogId,
        IEnumerable<ComponentDefinition> components,
        IEnumerable<FunctionDefinition> functions,
        string protocolVersion = A2UIProtocol.Version,
        string? instructions = null,
        JsonObject? document = null)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            throw new A2UICatalogException("A catalog needs a non-empty catalogId.");
        }

        CatalogId = catalogId;
        ProtocolVersion = protocolVersion;
        Instructions = instructions;
        Document = document;
        Components = components.ToDictionary(c => c.Name, StringComparer.Ordinal);
        Functions = functions.ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (string? name in Components.Keys.Concat(Functions.Keys))
        {
            if (!Uax31.IsIdentifier(name))
            {
                throw new A2UICatalogException($"Catalog entity name '{name}' is not a UAX #31 identifier.");
            }
        }

        if (Components.ContainsKey("Surface"))
        {
            throw new A2UICatalogException("The component name 'Surface' is reserved.");
        }

        foreach (FunctionDefinition f in Functions.Values)
        {
            if (f.RequiresUserActivation && f.AllowedCallers != AllowedCallers.RendererOnly)
            {
                throw new A2UICatalogException(
                    $"Function '{f.Name}' requires user activation, so allowedCallers must be rendererOnly.");
            }
        }
    }

    public string CatalogId { get; }

    public string ProtocolVersion { get; }

    public string? Instructions { get; }

    /// <summary>The catalog's JSON document, when it was loaded from one.</summary>
    public JsonObject? Document { get; }

    public IReadOnlyDictionary<string, ComponentDefinition> Components { get; }

    public IReadOnlyDictionary<string, FunctionDefinition> Functions { get; }

    /// <summary>Returns a copy of this catalog with the given implementations attached by function name.</summary>
    public Catalog WithFunctions(IReadOnlyDictionary<string, A2UIFunction> implementations)
    {
        List<FunctionDefinition> functions = Functions.Values.Select(f =>
            implementations.TryGetValue(f.Name, out A2UIFunction? impl) ? f.WithImplementation(impl) : f).ToList();
        foreach ((string name, A2UIFunction _) in implementations)
        {
            if (!Functions.ContainsKey(name))
            {
                throw new A2UICatalogException($"Catalog '{CatalogId}' does not declare function '{name}'.");
            }
        }

        return new Catalog(CatalogId, Components.Values, functions, ProtocolVersion, Instructions, Document);
    }

    /// <summary>Returns a copy of this catalog under a different id (useful for tests and aliases).</summary>
    public Catalog WithCatalogId(string catalogId) =>
        new(catalogId, Components.Values, Functions.Values, ProtocolVersion, Instructions, Document);

    /// <summary>Returns a copy of this catalog with extra component types or functions added.</summary>
    public Catalog Extend(IEnumerable<ComponentDefinition>? components = null, IEnumerable<FunctionDefinition>? functions = null)
    {
        Dictionary<string, ComponentDefinition> comps = Components.Values.ToDictionary(c => c.Name);
        foreach (ComponentDefinition c in components ?? [])
        {
            comps[c.Name] = c;
        }

        Dictionary<string, FunctionDefinition> funcs = Functions.Values.ToDictionary(f => f.Name);
        foreach (FunctionDefinition f in functions ?? [])
        {
            funcs[f.Name] = f;
        }

        return new Catalog(CatalogId, comps.Values, funcs.Values, ProtocolVersion, Instructions, Document);
    }

    public static Catalog FromJson(string json) =>
        FromJson(JsonNode.Parse(json) as JsonObject ?? throw new A2UICatalogException("A catalog must be a JSON object."));

    /// <summary>Loads a catalog from its JSON Schema document. Functions are schema-only until implementations are attached.</summary>
    public static Catalog FromJson(JsonObject document)
    {
        foreach ((string key, JsonNode? _) in document)
        {
            if (!s_allowedRootKeys.Contains(key))
            {
                throw new A2UICatalogException($"Catalog has an unsupported top-level key '{key}'.");
            }
        }

        string catalogId = document["catalogId"]?.GetValue<string>()
                        ?? throw new A2UICatalogException("Catalog is missing 'catalogId'.");
        string protocolVersion = document["protocolVersion"]?.GetValue<string>() ?? "0.9";
        CheckRefs(document, catalogId);

        List<ComponentDefinition> components = new();
        if (document["components"] is JsonObject comps)
        {
            foreach ((string name, JsonNode? schemaNode) in comps)
            {
                if (schemaNode is JsonObject schema)
                {
                    components.Add(ReadComponent(name, schema));
                }
            }
        }

        List<FunctionDefinition> functions = new();
        if (document["functions"] is JsonObject funcs)
        {
            foreach ((string name, JsonNode? schemaNode) in funcs)
            {
                if (schemaNode is not JsonObject schema)
                {
                    continue;
                }

                if (name.StartsWith('@'))
                {
                    throw new A2UICatalogException($"Catalog functions may not use the reserved '@' prefix ('{name}').");
                }

                functions.Add(new FunctionDefinition(name)
                {
                    ReturnType = schema["returnType"]?.GetValue<string>() ?? "any",
                    AllowedCallers = FunctionDefinition.ParseCallers(schema["allowedCallers"]?.GetValue<string>()),
                    RequiresUserActivation = schema["requiresUserActivation"]?.GetValue<bool>() ?? false,
                    ArgsSchema = schema["properties"]?["args"]?.DeepClone() as JsonObject,
                    Description = schema["description"]?.GetValue<string>(),
                });
            }
        }

        return new Catalog(
            catalogId,
            components,
            functions,
            protocolVersion,
            document["instructions"]?.GetValue<string>(),
            (JsonObject)document.DeepClone());
    }

    private static void CheckRefs(JsonNode? node, string catalogId)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out string? target))
                    {
                        if (target.StartsWith('#'))
                        {
                            continue;
                        }

                        const string prefix = "common_types.json#/$defs/";
                        if (!target.StartsWith(prefix, StringComparison.Ordinal) || !s_allowedCommonRefs.Contains(target[prefix.Length..]))
                        {
                            throw new A2UICatalogException(
                                $"Catalog '{catalogId}' references '{target}', which is not an allowed $ref target. Catalog references are never fetched.");
                        }
                    }
                    else
                    {
                        CheckRefs(value, catalogId);
                    }
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    CheckRefs(item, catalogId);
                }

                break;
        }
    }

    private static ComponentDefinition ReadComponent(string name, JsonObject schema)
    {
        HashSet<string> properties = new(StringComparer.Ordinal);
        Dictionary<string, JsonNode?> propertySchemas = new(StringComparer.Ordinal);
        HashSet<string> required = new(StringComparer.Ordinal);
        Dictionary<string, ChildReferenceKind> children = new(StringComparer.Ordinal);
        Dictionary<string, string> nestedKeys = new(StringComparer.Ordinal);
        bool checkable = false;

        // Required keys count only from the schema itself and allOf parts; anyOf/oneOf branches
        // are alternatives, so their required lists do not apply to every instance.
        void Visit(JsonObject s, bool mandatory)
        {
            if (s["$ref"] is JsonValue r && r.GetValue<string>().EndsWith("/Checkable", StringComparison.Ordinal))
            {
                checkable = true;
            }

            if (s["properties"] is JsonObject props)
            {
                foreach ((string prop, JsonNode? propSchema) in props)
                {
                    if (prop == "component")
                    {
                        continue;
                    }

                    if (prop == "@checks")
                    {
                        checkable = true;
                        continue;
                    }

                    properties.Add(prop);
                    propertySchemas.TryAdd(prop, propSchema);
                    if (ChildKind(propSchema, out string? nestedKey) is { } kind)
                    {
                        children[prop] = kind;
                        if (nestedKey is not null)
                        {
                            nestedKeys[prop] = nestedKey;
                        }
                    }
                }
            }

            if (mandatory && s["required"] is JsonArray req)
            {
                foreach (JsonNode? r2 in req)
                {
                    string n = r2!.GetValue<string>();
                    if (n != "component")
                    {
                        required.Add(n);
                    }
                }
            }

            foreach (string? combiner in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (s[combiner] is JsonArray parts)
                {
                    foreach (JsonObject part in parts.OfType<JsonObject>())
                    {
                        Visit(part, mandatory && combiner == "allOf");
                    }
                }
            }
        }

        Visit(schema, mandatory: true);
        return new ComponentDefinition(name)
        {
            Properties = properties,
            PropertySchemas = propertySchemas,
            Required = required,
            IsCheckable = checkable,
            ChildProperties = children,
            NestedChildKeys = nestedKeys,
            AllowedParents = ReadStringList(schema["allowedParents"]),
            AllowedChildren = ReadStringList(schema["allowedChildren"]),
            Schema = (JsonObject)schema.DeepClone(),
            Metadata = schema["metadata"]?.DeepClone() as JsonObject,
        };
    }

    private static ChildReferenceKind? ChildKind(JsonNode? propSchema, out string? nestedKey)
    {
        nestedKey = null;
        if (propSchema is not JsonObject s)
        {
            return null;
        }

        if (IsIdReference(s))
        {
            return ChildReferenceKind.Single;
        }

        if (s["$ref"] is JsonValue r && r.GetValue<string>().EndsWith("/ChildList", StringComparison.Ordinal))
        {
            return ChildReferenceKind.List;
        }

        if (s["items"] is JsonObject items)
        {
            if (IsIdReference(items))
            {
                return ChildReferenceKind.List;
            }

            if (items["properties"] is JsonObject itemProps)
            {
                foreach ((string key, JsonNode? inner) in itemProps)
                {
                    if (inner is JsonObject innerSchema && IsIdReference(innerSchema))
                    {
                        nestedKey = key;
                        return ChildReferenceKind.NestedSingle;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsIdReference(JsonObject s) =>
        s["$ref"] is JsonValue r && r.GetValue<string>() is var reference
        && (reference.EndsWith("/Child", StringComparison.Ordinal) || reference.EndsWith("/ComponentId", StringComparison.Ordinal));

    private static List<string>? ReadStringList(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(n => n!.GetValue<string>()).ToList() : null;
}
