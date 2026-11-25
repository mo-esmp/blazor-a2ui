using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Expressions;
using BlazorA2UI.Model;
using BlazorA2UI.Text;

namespace BlazorA2UI.Processing;

/// <summary>
/// Splits a v1.0 component object into its envelope (<c>id</c>, <c>component</c>,
/// <c>@catalogId</c>, <c>@accessibility</c>, <c>@metadata</c>, <c>@checks</c>) and its catalog
/// properties, and resolves its catalog.
/// </summary>
internal static class ComponentParser
{
    private static readonly JsonObject AccessibilitySchema = new() { ["$ref"] = "common_types.json#/$defs/AccessibilityAttributes" };

    private static readonly string[] EnvelopeKeys = ["catalogId", "accessibility", "metadata", "checks"];

    public static ComponentInstance Parse(JsonObject raw, Surface surface, CatalogRegistry catalogs, A2UIValidationConfig? validation = null)
    {
        var id = A2UIValues.AsString(raw["id"]) ?? throw new A2UIValidationException("A component needs a string 'id'.");
        if (!Uax31.IsIdentifier(id))
        {
            throw new A2UIValidationException($"Component id '{id}' must be a valid UAX #31 identifier.") { Path = "/id" };
        }

        var type = A2UIValues.AsString(raw["component"])
                   ?? throw new A2UIValidationException($"Component '{id}' needs a string 'component' type.");
        if (type == Composition.SurfaceType)
        {
            throw new A2UIValidationException("The 'Surface' component is created by createSurface and cannot be updated.");
        }

        // Resolve the catalog: @catalogId, else a legacy bare catalogId the default catalog's
        // schema does not declare, else the surface default.
        string? explicitCatalogId = null;
        var legacyEnvelope = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ContainsKey("@catalogId"))
        {
            explicitCatalogId = A2UIValues.AsString(raw["@catalogId"]) is { Length: > 0 } c
                ? c
                : throw new A2UIValidationException($"Component '{id}': '@catalogId' must be a non-empty string.");
        }

        ComponentDefinition? defaultDefinition = null;
        if (surface.CatalogId is { } surfaceCatalogId && surface.TryGetCatalog(surfaceCatalogId, out var surfaceCatalog))
        {
            surfaceCatalog.Components.TryGetValue(type, out defaultDefinition);
        }

        if (explicitCatalogId is null && raw.ContainsKey("catalogId") && IsEnvelopeKey(defaultDefinition, "catalogId"))
        {
            explicitCatalogId = A2UIValues.AsString(raw["catalogId"]) is { Length: > 0 } legacy
                ? legacy
                : throw new A2UIValidationException($"Component '{id}': 'catalogId' must be a non-empty string.");
            legacyEnvelope.Add("catalogId");
        }

        var catalogId = explicitCatalogId ?? surface.CatalogId
            ?? throw new A2UICatalogException(
                $"Component '{id}' names no @catalogId and surface '{surface.SurfaceId}' has no default catalogId.");
        if (!surface.TryGetCatalog(catalogId, out var catalog))
        {
            throw new A2UICatalogException($"Catalog not found: {catalogId}");
        }

        if (!catalog.Components.TryGetValue(type, out var definition))
        {
            if (validation is not null)
            {
                throw new A2UIValidationException($"Catalog '{catalogId}' has no component '{type}'.") { Path = "/component" };
            }

            // A streaming renderer keeps the component and shows a placeholder for it.
            definition = ComponentDefinition.Unknown(type);
        }

        foreach (var key in EnvelopeKeys)
        {
            if (key == "catalogId")
            {
                if (raw.ContainsKey("@catalogId") && raw.ContainsKey("catalogId") && IsEnvelopeKey(definition, "catalogId"))
                {
                    throw new A2UIValidationException($"Component '{id}' has both '@catalogId' and 'catalogId'.");
                }

                continue;
            }

            if (raw.ContainsKey(key) && IsEnvelopeKey(definition, key))
            {
                if (raw.ContainsKey("@" + key))
                {
                    throw new A2UIValidationException($"Component '{id}' has both '@{key}' and '{key}'.");
                }

                legacyEnvelope.Add(key);
            }
        }

        JsonNode? Envelope(string key) => raw.ContainsKey("@" + key) ? raw["@" + key] : legacyEnvelope.Contains(key) ? raw[key] : null;

        var accessibility = Envelope("accessibility");
        if (accessibility is not null and not JsonObject)
        {
            throw new A2UIValidationException($"Component '{id}': '@accessibility' must be an object.");
        }

        if (accessibility is JsonObject a11y)
        {
            foreach (var (key, _) in a11y)
            {
                if (key is not ("label" or "description" or "live" or "hidden"))
                {
                    throw new A2UIValidationException($"Component '{id}': unknown accessibility attribute '{key}'.");
                }
            }

            if (validation is not null && SchemaCheck.Validate(a11y, AccessibilitySchema, null) is { } a11yProblem)
            {
                throw new A2UIValidationException($"Component '{id}': '@accessibility' {a11yProblem}.") { Path = "/@accessibility" };
            }
        }

        var metadata = MessageProcessor.ReadMetadata(Envelope("metadata"), surface.SurfaceId, $"component '{id}' @metadata");

        var checks = Envelope("checks");
        if (checks is not null)
        {
            if (checks is not JsonArray)
            {
                throw new A2UIValidationException($"Component '{id}': '@checks' must be an array.");
            }

            if (!definition.IsCheckable)
            {
                throw new A2UIValidationException($"Component '{id}': '{type}' does not accept '@checks'.");
            }
        }

        foreach (var required in validation is null ? Enumerable.Empty<string>() : definition.Required)
        {
            if (!raw.ContainsKey(required))
            {
                throw new A2UIValidationException($"Component '{id}': '{type}' requires '{required}'.") { Path = "/" + required };
            }
        }

        var properties = new JsonObject();
        foreach (var (key, value) in raw)
        {
            if (key is "id" or "component" || legacyEnvelope.Contains(key))
            {
                continue;
            }

            if (key.StartsWith('@'))
            {
                if (key is "@catalogId" or "@accessibility" or "@metadata" or "@checks")
                {
                    continue;
                }

                throw new A2UIException(A2UIErrorCodes.InvalidReservedKey, $"Component '{id}' uses the reserved key '{key}'.");
            }

            if (validation is not null && !definition.IsUnknown)
            {
                if (!definition.Properties.Contains(key))
                {
                    throw new A2UIValidationException($"Component '{id}': '{type}' has no property '{key}'.") { Path = "/" + key };
                }

                if (SchemaCheck.Validate(value, definition.PropertySchemas.GetValueOrDefault(key), catalog.Document) is { } problem)
                {
                    throw new A2UIValidationException($"Component '{id}': invalid '{key}': {problem}") { Path = "/" + key };
                }
            }

            ValidateDynamic(value, id, key, 0);
            ValidateCalls(value, id, key, surface, catalogs, validation, 0);
            properties[key] = value?.DeepClone();
        }

        if (accessibility is not null)
        {
            ValidateDynamic(accessibility, id, "@accessibility", 0);
            ValidateCalls(accessibility, id, "@accessibility", surface, catalogs, validation, 0);
        }

        if (checks is not null)
        {
            ValidateDynamic(checks, id, "@checks", 0);
            ValidateCalls(checks, id, "@checks", surface, catalogs, validation, 0);
        }

        return new ComponentInstance(
            id,
            type,
            catalog,
            definition,
            properties,
            (JsonObject?)accessibility?.DeepClone(),
            metadata,
            (JsonArray?)checks?.DeepClone(),
            explicitCatalogId);
    }

    /// <summary>
    /// Whether a bare <c>catalogId</c>, <c>accessibility</c>, <c>metadata</c> or <c>checks</c> key is the
    /// legacy spelling of the <c>@</c> envelope key rather than a component property. It is the
    /// envelope key when the component does not declare it, or (for <c>catalogId</c> and
    /// <c>metadata</c>) when the component inlines the standard common properties: it declares
    /// <c>id</c> beside it and the property has the standard shape.
    /// </summary>
    private static bool IsEnvelopeKey(ComponentDefinition? definition, string key)
    {
        if (definition is null || !definition.Properties.Contains(key))
        {
            return true;
        }

        if (!definition.Properties.Contains("id") || definition.PropertySchemas.GetValueOrDefault(key) is not JsonObject schema)
        {
            return false;
        }

        return key switch
        {
            "catalogId" => A2UIValues.AsString(schema["type"]) == "string",
            "metadata" => A2UIValues.AsString(schema["type"]) == "object"
                          && schema["properties"] is JsonObject props && props.Count == 1 && props.ContainsKey("extensions"),
            _ => false,
        };
    }

    /// <summary>
    /// Static checks on function calls whose problems do not depend on data: every call must
    /// resolve to a catalog the surface has; under strict validation the function must be declared
    /// there; renderer content must not call <c>agentOnly</c> functions; literal arguments must fit
    /// the function's argument schema; and <c>formatString</c> templates must parse.
    /// </summary>
    private static void ValidateCalls(
        JsonNode? node, string componentId, string property, Surface surface, CatalogRegistry catalogs, A2UIValidationConfig? validation, int depth) =>
        ValidateCalls(node, componentId, "/" + Data.JsonPointer.Escape(property), surface, validation, depth);

    private static void ValidateCalls(
        JsonNode? node, string componentId, string pointer, Surface surface, A2UIValidationConfig? validation, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj when obj.ContainsKey("@call"):
                var name = A2UIValues.AsString(obj["@call"]);
                var args = obj["args"] as JsonObject;
                if (name is null || (!name.StartsWith('@') && !Uax31.IsIdentifier(name)))
                {
                    throw new A2UIValidationException(
                        $"Component '{componentId}': function name '{name}' must be a valid UAX #31 identifier.") { Path = pointer + "/@call" };
                }

                if (!name.StartsWith('@'))
                {
                    if (obj.ContainsKey("catalogId") && A2UIValues.AsString(obj["catalogId"]) is not { Length: > 0 })
                    {
                        throw new A2UICatalogException($"Component '{componentId}': a function call's catalogId must be a non-empty string.")
                        {
                            Path = pointer + "/catalogId",
                        };
                    }

                    var explicitId = A2UIValues.AsString(obj["catalogId"]);
                    var catalogId = explicitId ?? surface.CatalogId
                        ?? throw new A2UICatalogException(
                            $"Component '{componentId}': '{name}' names no catalogId and the surface has no default catalogId.") { Path = pointer };
                    if (!surface.TryGetCatalog(catalogId, out var catalog))
                    {
                        throw new A2UICatalogException($"Component '{componentId}': Catalog not found: {catalogId}") { Path = pointer };
                    }

                    if (catalog.Functions.TryGetValue(name, out var function))
                    {
                        if (function.AllowedCallers == AllowedCallers.AgentOnly)
                        {
                            throw new A2UIValidationException(
                                $"Component '{componentId}': function '{name}' is agentOnly and cannot be called by renderer content.") { Path = pointer };
                        }

                        if (args is not null && function.ArgsSchema is { } schema && ArgsSchema.ValidateLiterals(schema, args) is var (argument, problem))
                        {
                            throw new A2UIValidationException($"Component '{componentId}': invalid arguments for '{name}': {problem}")
                            {
                                Path = $"{pointer}/args/{Data.JsonPointer.Escape(argument)}",
                            };
                        }

                        if (name == "formatString" && A2UIValues.AsString(args?["value"]) is { } template)
                        {
                            try
                            {
                                ExpressionParser.Parse(template);
                            }
                            catch (A2UIExpressionException ex)
                            {
                                throw new A2UIValidationException($"Component '{componentId}': invalid formatString template: {ex.Message}")
                                {
                                    Path = pointer + "/args/value",
                                };
                            }
                        }
                    }
                    else if (validation is { AllowUnknownElements: false })
                    {
                        throw new A2UIValidationException(
                            $"Component '{componentId}': Unrecognized function '{name}' (catalog '{catalogId}' does not declare it).") { Path = pointer };
                    }
                }

                if (args is not null)
                {
                    foreach (var (key, value) in args)
                    {
                        ValidateCalls(value, componentId, $"{pointer}/args/{Data.JsonPointer.Escape(key)}", surface, validation, depth + 1);
                    }
                }

                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    ValidateCalls(value, componentId, $"{pointer}/{Data.JsonPointer.Escape(key)}", surface, validation, depth + 1);
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    ValidateCalls(arr[i], componentId, $"{pointer}/{i}", surface, validation, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Rejects unescaped single-<c>@</c> keys other than the <c>@path</c> and <c>@call</c>
    /// directives, and malformed directive objects, anywhere inside a property value.
    /// </summary>
    internal static void ValidateDynamic(JsonNode? node, string componentId, string property, int depth)
    {
        if (depth > 64)
        {
            throw new A2UIValidationException($"Component '{componentId}': '{property}' is nested too deeply.");
        }

        switch (node)
        {
            case JsonObject obj when obj.ContainsKey("@path"):
                if (obj.Count != 1 || A2UIValues.AsString(obj["@path"]) is null)
                {
                    throw new A2UIValidationException($"Component '{componentId}': a '@path' binding in '{property}' must hold only a string '@path'.");
                }

                break;
            case JsonObject obj when obj.ContainsKey("@call"):
                foreach (var (key, value) in obj)
                {
                    if (key is not ("@call" or "catalogId" or "args"))
                    {
                        throw new A2UIValidationException($"Component '{componentId}': unexpected key '{key}' in a function call in '{property}'.");
                    }
                }

                if (A2UIValues.AsString(obj["@call"]) is not { Length: > 0 })
                {
                    throw new A2UIValidationException($"Component '{componentId}': the function name in '{property}' must be a valid UAX #31 identifier.");
                }

                if (obj.ContainsKey("catalogId") && A2UIValues.AsString(obj["catalogId"]) is not { Length: > 0 })
                {
                    if (obj["catalogId"] is JsonValue cv && cv.TryGetValue<string>(out _))
                    {
                        throw new A2UICatalogException($"Component '{componentId}': Catalog not found: '' (the catalogId in '{property}' is empty).");
                    }

                    throw new A2UIValidationException($"Component '{componentId}': 'catalogId' in '{property}' must be a string.");
                }

                if (obj["args"] is { } args)
                {
                    if (args is not JsonObject argsObject)
                    {
                        throw new A2UIValidationException($"Component '{componentId}': 'args' in '{property}' must be an object.");
                    }

                    foreach (var (_, value) in argsObject)
                    {
                        ValidateDynamic(value, componentId, property, depth + 1);
                    }
                }

                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.Length > 0 && key[0] == '@' && (key.Length == 1 || key[1] != '@'))
                    {
                        throw new A2UIValidationException(
                            $"Component '{componentId}': '{key}' in '{property}' uses the reserved '@' prefix; escape a literal key as '@{key}'.");
                    }

                    ValidateDynamic(value, componentId, property, depth + 1);
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    ValidateDynamic(item, componentId, property, depth + 1);
                }

                break;
        }
    }
}
