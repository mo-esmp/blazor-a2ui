using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Text;

namespace BlazorA2UI.Catalogs;

/// <summary>
/// Checks a component property value against its catalog schema: the subset of JSON Schema
/// that A2UI catalogs use (<c>$ref</c> into <c>common_types.json</c> or the catalog's own
/// <c>$defs</c>, types, enums, objects, arrays and the combinators). Function calls are only
/// checked for their shape here; the call itself is checked against its catalog separately.
/// </summary>
internal static class SchemaCheck
{
    private const string CommonTypesName = "common_types.json";
    private const int MaxDepth = 64;

    private static readonly Lazy<JsonObject> CommonTypes = new(() =>
    {
        using var stream = typeof(SchemaCheck).Assembly.GetManifestResourceStream("BlazorA2UI.Schemas.common_types.json")
                           ?? throw new InvalidOperationException("The embedded common_types.json schema is missing.");
        return (JsonObject)JsonNode.Parse(stream)!;
    });

    /// <summary>Returns the first problem, or null when the value fits the schema.</summary>
    /// <param name="value">The property value as the agent sent it.</param>
    /// <param name="schema">The property's schema.</param>
    /// <param name="catalogDocument">The catalog document, for local <c>#/$defs/...</c> references.</param>
    public static string? Validate(JsonNode? value, JsonNode? schema, JsonObject? catalogDocument) =>
        Check(value, schema, catalogDocument ?? new JsonObject(), 0);

    private static string? Check(JsonNode? value, JsonNode? schemaNode, JsonObject document, int depth)
    {
        if (depth > MaxDepth)
        {
            return "the value is nested too deeply";
        }

        switch (schemaNode)
        {
            case null:
                return null;
            case JsonValue b when b.GetValueKind() == JsonValueKind.True:
                return null;
            case JsonValue b when b.GetValueKind() == JsonValueKind.False:
                return "no value is allowed here";
            case not JsonObject:
                return null;
        }

        var schema = (JsonObject)schemaNode;

        if (A2UIValues.AsString(schema["$ref"]) is { } reference)
        {
            if (reference.EndsWith("/FunctionCall", StringComparison.Ordinal))
            {
                if (value is not JsonObject call || A2UIValues.AsString(call["@call"]) is null)
                {
                    return "expected a function call";
                }
            }
            else if (reference.Contains("anyFunction", StringComparison.Ordinal))
            {
                // Function-specific argument schemas are checked where the call is resolved.
            }
            else if (Resolve(reference, document, out var target, out var targetDocument))
            {
                if (Check(value, target, targetDocument, depth + 1) is { } problem)
                {
                    return problem;
                }
            }
        }

        if (schema["type"] is { } type && !TypeMatches(value, type))
        {
            return $"expected {DescribeType(type)}, got {Describe(value)}";
        }

        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value))
        {
            return $"expected {schema["const"]?.ToJsonString() ?? "null"}";
        }

        if (schema["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            return $"{Describe(value)} is not one of {options.ToJsonString()}";
        }

        if (value is JsonObject obj && ObjectProblem(obj, schema, document, depth) is { } objectProblem)
        {
            return objectProblem;
        }

        if (value is JsonArray arr && ArrayProblem(arr, schema, document, depth) is { } arrayProblem)
        {
            return arrayProblem;
        }

        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var s = v.GetValue<string>();
            if (A2UIValues.AsString(schema["pattern"]) is { } pattern && !PatternMatches(pattern, s))
            {
                return $"'{s}' does not match the required pattern";
            }
        }

        if (value is JsonValue n && n.GetValueKind() == JsonValueKind.Number)
        {
            var d = A2UIValues.ToDouble(n) ?? 0;
            if (A2UIValues.ToDouble(schema["minimum"]) is { } min && d < min)
            {
                return $"{d} is below the minimum {min}";
            }

            if (A2UIValues.ToDouble(schema["maximum"]) is { } max && d > max)
            {
                return $"{d} is above the maximum {max}";
            }
        }

        if (schema["allOf"] is JsonArray all)
        {
            foreach (var part in all)
            {
                if (Check(value, part, document, depth + 1) is { } problem)
                {
                    return problem;
                }
            }
        }

        foreach (var combiner in new[] { "anyOf", "oneOf" })
        {
            if (schema[combiner] is JsonArray branches && branches.Count > 0)
            {
                string? first = null;
                var matched = false;
                foreach (var branch in branches)
                {
                    var problem = Check(value, branch, document, depth + 1);
                    if (problem is null)
                    {
                        matched = true;
                        break;
                    }

                    first ??= problem;
                }

                if (!matched)
                {
                    return branches.Count == 1 ? first : $"the value matches none of the allowed forms ({first})";
                }
            }
        }

        if (schema["not"] is JsonObject not && Check(value, not, document, depth + 1) is null)
        {
            return "the value has a form that is not allowed here";
        }

        return null;
    }

    private static string? ObjectProblem(JsonObject obj, JsonObject schema, JsonObject document, int depth)
    {
        var props = schema["properties"] as JsonObject;
        if (schema["required"] is JsonArray required)
        {
            foreach (var r in required)
            {
                var key = A2UIValues.AsString(r);
                if (key is not null && !obj.ContainsKey(key))
                {
                    return $"missing required '{key}'";
                }
            }
        }

        foreach (var (key, value) in obj)
        {
            if (props?[key] is { } propSchema)
            {
                if (Check(value, propSchema, document, depth + 1) is { } problem)
                {
                    return $"'{key}': {problem}";
                }

                continue;
            }

            if (schema["propertyNames"] is JsonObject names && Check(key, names, document, depth + 1) is not null)
            {
                return $"'{key}' is not an allowed key";
            }

            if (schema["additionalProperties"] is { } additional)
            {
                if (additional is JsonValue f && f.GetValueKind() == JsonValueKind.False)
                {
                    return $"unexpected key '{key}'";
                }

                if (Check(value, additional, document, depth + 1) is { } problem)
                {
                    return $"'{key}': {problem}";
                }
            }
            else if (schema["unevaluatedProperties"] is JsonValue u && u.GetValueKind() == JsonValueKind.False
                     && !DeclaredKeys(schema, document, 0).Contains(key))
            {
                return $"unexpected key '{key}'";
            }
        }

        return null;
    }

    /// <summary>Keys declared by a schema and the parts it combines (for <c>unevaluatedProperties</c>).</summary>
    private static HashSet<string> DeclaredKeys(JsonObject schema, JsonObject document, int depth)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (depth > 16)
        {
            return keys;
        }

        if (schema["properties"] is JsonObject props)
        {
            keys.UnionWith(props.Select(p => p.Key));
        }

        if (A2UIValues.AsString(schema["$ref"]) is { } reference
            && Resolve(reference, document, out var target, out var targetDocument) && target is JsonObject targetSchema)
        {
            keys.UnionWith(DeclaredKeys(targetSchema, targetDocument, depth + 1));
        }

        foreach (var combiner in new[] { "allOf", "anyOf", "oneOf" })
        {
            foreach (var part in (schema[combiner] as JsonArray ?? []).OfType<JsonObject>())
            {
                keys.UnionWith(DeclaredKeys(part, document, depth + 1));
            }
        }

        return keys;
    }

    private static string? ArrayProblem(JsonArray arr, JsonObject schema, JsonObject document, int depth)
    {
        if (A2UIValues.ToDouble(schema["minItems"]) is { } minItems && arr.Count < minItems)
        {
            return $"needs at least {minItems} items";
        }

        if (A2UIValues.ToDouble(schema["maxItems"]) is { } maxItems && arr.Count > maxItems)
        {
            return $"allows at most {maxItems} items";
        }

        if (schema["items"] is JsonObject items)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (Check(arr[i], items, document, depth + 1) is { } problem)
                {
                    return $"item {i}: {problem}";
                }
            }
        }

        return null;
    }

    private static bool Resolve(string reference, JsonObject document, out JsonNode? target, out JsonObject targetDocument)
    {
        target = null;
        targetDocument = document;
        var hash = reference.IndexOf('#');
        var file = hash < 0 ? reference : reference[..hash];
        var pointer = hash < 0 ? string.Empty : reference[(hash + 1)..];
        if (file.EndsWith(CommonTypesName, StringComparison.Ordinal))
        {
            targetDocument = CommonTypes.Value;
        }
        else if (file.Length > 0)
        {
            return false; // Another document (for example catalog.json): not resolvable here.
        }

        JsonNode? node = targetDocument;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = (node as JsonObject)?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            if (node is null)
            {
                return false;
            }
        }

        target = node;
        return true;
    }

    private static bool TypeMatches(JsonNode? value, JsonNode type) => type switch
    {
        JsonArray types => types.Any(t => TypeMatches(value, t!)),
        JsonValue t when t.TryGetValue<string>(out var name) => name switch
        {
            "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
            "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
            "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && A2UIValues.ToDouble(v) % 1 == 0,
            "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "array" => value is JsonArray,
            "object" => value is JsonObject,
            "null" => value is null || value is JsonValue v && v.GetValueKind() == JsonValueKind.Null,
            _ => true,
        },
        _ => true,
    };

    private static bool PatternMatches(string pattern, string value)
    {
        if (pattern.Contains(@"\p{XID_Start}", StringComparison.Ordinal))
        {
            return Uax31.IsIdentifier(value);
        }

        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return true; // A pattern .NET cannot compile is not enforced.
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string DescribeType(JsonNode type) =>
        type is JsonArray a ? string.Join(" or ", a.Select(t => t?.ToString())) : $"{type}";

    private static string Describe(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            _ => "null",
        },
        _ => "a value",
    };
}
