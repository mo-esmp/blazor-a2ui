using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// A small JSON Schema subset for function arguments: required keys, closed objects,
/// primitive types, enums, numeric minimums, array lengths, defaults, and
/// <c>DynamicBoolean</c> coercion. Arguments are checked after bindings resolve.
/// </summary>
internal static class ArgsSchema
{
    public static void ApplyDefaultsAndCoerce(JsonObject schema, JsonObject args)
    {
        if (schema["properties"] is not JsonObject props)
        {
            return;
        }

        foreach (var (name, propNode) in props)
        {
            if (propNode is not JsonObject prop)
            {
                continue;
            }

            if (!args.ContainsKey(name) && prop["default"] is { } def)
            {
                args[name] = def.DeepClone();
            }

            if (args[name] is JsonObject nested && prop["properties"] is JsonObject)
            {
                // Defaults declared inside an object argument apply too.
                ApplyDefaultsAndCoerce(prop, nested);
            }

            if (!args.ContainsKey(name))
            {
                continue;
            }

            if (IsRef(prop, "DynamicBoolean"))
            {
                args[name] = A2UIValues.ToBoolean(args[name]);
            }
            else if (prop["items"] is JsonObject items && IsRef(items, "DynamicBoolean") && args[name] is JsonArray arr)
            {
                var coerced = new JsonArray();
                foreach (var item in arr)
                {
                    coerced.Add(A2UIValues.ToBoolean(item));
                }

                args[name] = coerced;
            }
        }
    }

    /// <summary>
    /// Checks only the arguments that are plain literals (no <c>@path</c> or <c>@call</c> inside),
    /// so obviously wrong calls are rejected when the component arrives instead of at render time.
    /// </summary>
    public static (string Argument, string Problem)? ValidateLiterals(JsonObject schema, JsonObject args)
    {
        if (schema["properties"] is not JsonObject props)
        {
            return null;
        }

        foreach (var (name, value) in args)
        {
            if (props[name] is not JsonObject prop || IsDynamic(value))
            {
                continue;
            }

            var coerced = IsRef(prop, "DynamicBoolean") ? A2UIValues.ToBoolean(value) : value;
            if (CheckValue(prop, coerced, name, literal: true) is { } problem)
            {
                return (name, problem);
            }
        }

        return null;
    }

    private static bool IsDynamic(JsonNode? node) => node switch
    {
        JsonObject o => o.ContainsKey("@path") || o.ContainsKey("@call") || o.Any(p => IsDynamic(p.Value)),
        JsonArray a => a.Any(IsDynamic),
        _ => false,
    };

    /// <summary>Returns a description of the first problem, or null when the arguments are valid.</summary>
    public static string? Validate(JsonObject schema, JsonObject args)
    {
        var props = schema["properties"] as JsonObject;
        if (schema["required"] is JsonArray required)
        {
            foreach (var r in required)
            {
                var key = r!.GetValue<string>();
                if (!args.ContainsKey(key))
                {
                    return $"missing required argument '{key}'";
                }
            }
        }

        var closed = schema["additionalProperties"] is JsonValue ap && ap.GetValueKind() == JsonValueKind.False
                     || schema["unevaluatedProperties"] is JsonValue up && up.GetValueKind() == JsonValueKind.False;
        foreach (var (key, value) in args)
        {
            if (props?[key] is JsonObject prop)
            {
                var problem = CheckValue(prop, value, key);
                if (problem is not null)
                {
                    return problem;
                }
            }
            else if (closed && props is not null && !props.ContainsKey(key))
            {
                return $"unexpected argument '{key}'";
            }
        }

        // "anyOf" groups of required arguments are not enforced at run time: the reference renderer
        // evaluates such calls leniently (numeric with neither min nor max just checks the value parses).
        return null;
    }

    private static string? CheckValue(JsonObject prop, JsonNode? value, string key, bool literal = false)
    {
        if (value is null)
        {
            return null;
        }

        var kind = value.GetValueKind();
        if (prop["type"] is JsonValue t)
        {
            var ok = t.GetValue<string>() switch
            {
                "string" => kind == JsonValueKind.String,
                "number" => kind == JsonValueKind.Number,
                "integer" => kind == JsonValueKind.Number && A2UIValues.ToDouble(value) is { } d && d == Math.Floor(d),
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "array" => kind == JsonValueKind.Array,
                "object" => kind == JsonValueKind.Object,
                _ => true,
            };
            if (!ok)
            {
                return $"argument '{key}' must be of type {t.GetValue<string>()}";
            }
        }
        else if (IsRef(prop, "DynamicNumber")
                 && (kind is not (JsonValueKind.Number or JsonValueKind.String) || literal && A2UIValues.ToDouble(value) is null))
        {
            return $"argument '{key}' must be a number";
        }

        if (prop["minimum"] is JsonValue min && A2UIValues.ToDouble(value) is { } n && n < A2UIValues.ToDouble(min))
        {
            return $"argument '{key}' must be at least {min}";
        }

        if (prop["minItems"] is JsonValue minItems && value is JsonArray arr && arr.Count < minItems.GetValue<int>())
        {
            return $"argument '{key}' needs at least {minItems} items";
        }

        if (prop["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            return $"argument '{key}' is not one of the allowed values";
        }

        return null;
    }

    private static bool IsRef(JsonObject schema, string typeName) =>
        A2UIValues.AsString(schema["$ref"]) is { } r && r.EndsWith("/" + typeName, StringComparison.Ordinal);
}
