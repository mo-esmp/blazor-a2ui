using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs the <c>process_messages</c> and <c>validate</c> cases of the shared A2UI conformance
/// suite through <see cref="MessageProcessor"/>, following the reference Python harness:
/// catalogs from the case, a validation config from <c>strictMode</c> / <c>validationConfig</c>,
/// errors either thrown or reported on the error channel, and surface state compared afterwards.
/// </summary>
public class ProcessingConformanceTests
{
    private static readonly string[] Files =
    [
        "message_processor_v1_0.yaml",
        "validator_v1_0.yaml",
        "composition_constraints.yaml",
        "error_responses.yaml",
        "multi_catalog.yaml",
        "add_catalog.yaml",
        "expressions.yaml",
        "functions.yaml",
        "reserved_keys.yaml",
        "validation_result.yaml",
        "index_function.yaml",
        "data_deletion.yaml",
        "data_model_pointers.yaml",
    ];

    private const string BasicV09Id = "https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var file in Files)
        {
            foreach (var c in ConformanceData.Load(file))
            {
                var action = Str(c["action"]);
                if (action is "process_messages" or "validate" && IsV10(c))
                {
                    yield return [file, c["name"]!.GetValue<string>()];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Process(string file, string name)
    {
        var c = ConformanceData.Case(file, name);
        var isValidate = Str(c["action"]) == "validate";
        var stepCatalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in (c["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var catalog in LoadPaths(step["addCatalogPaths"] as JsonArray, out _))
            {
                stepCatalogIds.Add(catalog.CatalogId);
            }
        }

        var processor = new MessageProcessor(BuildCatalogs(c, stepCatalogIds), new A2UIProcessorOptions
        {
            Validation = CaseValidation(c, isValidate),
        });
        var errors = new List<A2UIErrorReport>();
        var outbound = new List<JsonObject>();
        processor.ErrorReported += errors.Add;
        processor.Outbound = m =>
        {
            outbound.Add(m.Message);
            return ValueTask.CompletedTask;
        };

        var steps = c["steps"] is JsonArray s
            ? s.OfType<JsonObject>().ToList()
            : [c];
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var isLast = i == steps.Count - 1;
            var expectError = ExpectedError(step["expectError"] ?? step["expect_error"])
                              ?? (isLast || ReferenceEquals(step, c) ? ExpectedError(c["expectError"] ?? c["expect_error"]) : null);
            var expect = step["expect"] as JsonObject ?? (isLast ? c["expect"] as JsonObject : null);
            A2UIValidationConfig? stepValidation = step["validationConfig"] is JsonObject vc && !ReferenceEquals(step, c)
                ? FromFlags(vc)
                : null;

            errors.Clear();
            outbound.Clear();
            Exception? thrown = null;
            try
            {
                foreach (var catalog in LoadPaths(step["addCatalogPaths"] as JsonArray, out _))
                {
                    processor.AddCatalog(catalog);
                }

                var messages = step["messages"] ?? step["payload"] ?? (step["message"] is { } one ? new JsonArray(one.DeepClone()) : null);
                if (messages is not null)
                {
                    processor.ProcessOrThrow(messages.DeepClone(), stepValidation);
                }
            }
            catch (A2UIException ex)
            {
                thrown = ex;
                processor.ReportError(ex, ex.SurfaceId);
            }

            if (isValidate || expectError is not null)
            {
                foreach (var surface in processor.Surfaces.Values)
                {
                    RenderAll(surface);
                }
            }

            if (expectError is not null)
            {
                // Errors in the order they happened (a thrown error is reported last).
                var first = errors.Select(e => new A2UIException(e.Code, e.Message)).FirstOrDefault() ?? thrown;
                ConformanceAssert.Error(expectError, first, name);
                if (expectError["rendererError"] is JsonObject rendererError && Str(rendererError["version"]) == "1.0")
                {
                    AssertRendererError(rendererError["error"]!.AsObject(), outbound, name);
                }

                continue;
            }

            Assert.True(
                thrown is null && errors.Count == 0,
                $"{name}: unexpected errors: {thrown?.Message} {string.Join("; ", errors.Select(e => e.Code + " " + e.Message))}");
            if (expect?["surfaces"] is JsonObject surfaces)
            {
                foreach (var (surfaceId, expected) in surfaces)
                {
                    AssertSurface(processor, surfaceId, expected!.AsObject(), name);
                }
            }
        }
    }

    private static JsonObject? ExpectedError(JsonNode? node) => node switch
    {
        JsonObject o => o,
        JsonValue v when v.TryGetValue<string>(out var message) => new JsonObject { ["message"] = message },
        _ => null,
    };

    // ------------------------------------------------------------------ catalogs

    private static bool IsV10(JsonObject c)
    {
        var version = Str(c["protocolVersion"]) ?? Str((c["catalog"] as JsonObject)?["protocolVersion"]);
        if (version is null && c["catalogs"] is JsonArray list)
        {
            version = list.OfType<JsonObject>().Select(i => Str(i["protocolVersion"])).FirstOrDefault(v => v is not null);
        }

        if (version is null)
        {
            var first = FirstMessage(c);
            version = Str(first?["version"]);
        }

        return version is "1.0" or "v1.0";
    }

    private static JsonObject? FirstMessage(JsonObject c)
    {
        var messages = c["messages"] ?? c["payload"] ?? (c["steps"] as JsonArray)?.OfType<JsonObject>().Select(s => s["messages"] ?? s["payload"]).FirstOrDefault(m => m is not null);
        while (messages is JsonArray a)
        {
            messages = a.FirstOrDefault();
        }

        return messages as JsonObject;
    }

    private static List<Catalog> BuildCatalogs(JsonObject c, HashSet<string> stepCatalogIds)
    {
        var map = new Dictionary<string, Catalog>(StringComparer.Ordinal)
        {
            [TestCatalogs.Basic.CatalogId] = TestCatalogs.Basic,
            ["basic"] = TestCatalogs.Basic.WithCatalogId("basic"),
        };
        var known = new HashSet<string>(map.Keys, StringComparer.Ordinal) { BasicV09Id };

        if (c["catalog"] is JsonObject single)
        {
            if (single["catalogSchema"] is JsonObject schema)
            {
                var id = Str(c["catalogId"]) ?? Str(single["catalogId"]) ?? Str(schema["catalogId"]) ?? $"catalog-{Str(c["name"])}";
                Add(map, known, FromSchema(schema, id));
            }
            else if (single.ContainsKey("components") || single.ContainsKey("catalogId"))
            {
                var id = Str(single["catalogId"]) ?? Str(c["catalogId"]) ?? $"catalog-{Str(c["name"])}";
                Add(map, known, Inline(single, id));
            }
        }

        foreach (var item in (c["catalogs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (item["catalogSchema"] is JsonObject schema)
            {
                var id = Str(schema["catalogId"]) ?? Str(item["catalogId"]);
                if (id is not null)
                {
                    Add(map, known, FromSchema(schema, id));
                }
            }
            else if (Str(item["catalogId"]) is { } id)
            {
                if (Str(item["protocolVersion"]) is { } v && v is not ("1.0" or "v1.0"))
                {
                    known.Add(id);
                    continue;
                }

                Add(map, known, Inline(item, id));
            }
        }

        foreach (var catalog in LoadPaths(c["catalogPaths"] as JsonArray, out var skipped))
        {
            Add(map, known, catalog);
            if (catalog.CatalogId != TestCatalogs.Basic.CatalogId && catalog.CatalogId != "test-catalog")
            {
                Add(map, known, catalog.WithCatalogId("test-catalog"));
            }
        }

        var expectsCatalogError = Str((c["expectError"] as JsonObject)?["category"]) == "CatalogError";
        if (!expectsCatalogError)
        {
            foreach (var id in CreateSurfaceCatalogIds(c))
            {
                if (!known.Contains(id) && !stepCatalogIds.Contains(id))
                {
                    Add(map, known, ComponentsOnly(id));
                }
            }
        }

        return map.Values.Distinct().ToList();
    }

    private static void Add(Dictionary<string, Catalog> map, HashSet<string> known, Catalog catalog)
    {
        map[catalog.CatalogId] = catalog;
        known.Add(catalog.CatalogId);
    }

    private static IEnumerable<string> CreateSurfaceCatalogIds(JsonObject c)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(c["messages"]);
        stack.Push(c["payload"]);
        foreach (var step in (c["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            stack.Push(step["messages"]);
            stack.Push(step["payload"]);
            stack.Push(step["message"]);
        }

        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonArray a:
                    foreach (var item in a)
                    {
                        stack.Push(item);
                    }

                    break;
                case JsonObject o:
                    if (o["messages"] is { } nested)
                    {
                        stack.Push(nested);
                    }

                    if ((o["createSurface"] as JsonObject)?["catalogId"] is JsonValue v && v.TryGetValue<string>(out var id))
                    {
                        yield return id;
                    }

                    break;
            }
        }
    }

    private static Catalog FromSchema(JsonObject schema, string id)
    {
        var doc = (JsonObject)schema.DeepClone();
        doc["catalogId"] = id;
        doc["protocolVersion"] ??= "1.0";
        return Catalog.FromJson(doc);
    }

    private static Catalog Inline(JsonObject spec, string id)
    {
        if (spec["components"] is null && spec["functions"] is null)
        {
            return ComponentsOnly(id);
        }

        var doc = new JsonObject { ["catalogId"] = id, ["protocolVersion"] = "1.0" };
        foreach (var key in new[] { "$schema", "components", "functions", "$defs" })
        {
            if (spec[key] is { } value)
            {
                doc[key] = value.DeepClone();
            }
        }

        if (spec["functions"] is null && spec["$defs"] is null)
        {
            doc["$defs"] = new JsonObject { ["anyFunction"] = new JsonObject { ["not"] = new JsonObject() } };
        }

        return Catalog.FromJson(doc);
    }

    /// <summary>A stand-in catalog with the basic components and no functions.</summary>
    private static Catalog ComponentsOnly(string id) =>
        new(id, TestCatalogs.Basic.Components.Values, [], "1.0");

    private static List<Catalog> LoadPaths(JsonArray? paths, out List<string> skipped)
    {
        skipped = [];
        var catalogs = new List<Catalog>();
        foreach (var p in paths ?? [])
        {
            var path = p!.GetValue<string>();
            if (path.Contains("basic/v1/", StringComparison.Ordinal) || path == TestCatalogs.Basic.CatalogId)
            {
                catalogs.Add(TestCatalogs.Basic);
            }
            else if (path.Contains("basic/", StringComparison.Ordinal) || path.Contains("v0_9", StringComparison.Ordinal))
            {
                skipped.Add(path);
            }
            else
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Conformance", "data", "catalogs", Path.GetFileName(path));
                catalogs.Add(Catalog.FromJson(File.ReadAllText(file)));
            }
        }

        return catalogs;
    }

    // ------------------------------------------------------------------ validation config

    private static A2UIValidationConfig? CaseValidation(JsonObject c, bool isValidate)
    {
        if (c["validationConfig"] is JsonObject flags)
        {
            return FromFlags(flags);
        }

        var strict = c["strictMode"] ?? c["strict_mode"] ?? (c["options"] as JsonObject)?["strict_mode"];
        var isStrict = strict is JsonValue sv && sv.GetValueKind() == JsonValueKind.True;
        var isRelaxed = strict is JsonValue rv && rv.GetValueKind() == JsonValueKind.False;
        if (isValidate)
        {
            return isRelaxed ? A2UIValidationConfig.Relaxed : A2UIValidationConfig.Strict;
        }

        return isStrict ? A2UIValidationConfig.Strict : null;
    }

    private static A2UIValidationConfig FromFlags(JsonObject flags) => new()
    {
        AllowOrphanComponents = Flag(flags, "allowOrphanComponents"),
        AllowDanglingReferences = Flag(flags, "allowDanglingReferences"),
        AllowMissingRoot = Flag(flags, "allowMissingRoot"),
        AllowUnknownElements = Flag(flags, "allowUnknownElements"),
    };

    private static bool Flag(JsonObject flags, string name) =>
        flags[name] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    // ------------------------------------------------------------------ assertions

    private static void AssertRendererError(JsonObject expected, List<JsonObject> outbound, string name)
    {
        var error = outbound.Select(m => m["error"] as JsonObject).LastOrDefault(e => e is not null);
        Assert.True(error is not null, $"{name}: expected an outbound error message");
        Assert.Equal(Str(expected["code"]), Str(error!["code"]));
        if (Str(expected["message"]) is { } message)
        {
            Assert.True(ConformanceAssert.MessageMatches(message, Str(error["message"]) ?? string.Empty), $"{name}: error.message expected '{message}', got '{Str(error["message"])}'");
        }

        foreach (var key in new[] { "surfaceId", "functionCallId", "path" })
        {
            if (Str(expected[key]) is { } value)
            {
                var actual = Str(error[key]);
                // As in the reference harness, a missing required property may be reported at the
                // property rather than at the component.
                var missingField = key == "path" && actual is not null && actual.StartsWith(value + "/", StringComparison.Ordinal)
                                   && (Str(error["message"]) ?? string.Empty).Contains($"requires '{actual[(value.Length + 1)..]}'", StringComparison.Ordinal);
                Assert.True(value == actual || missingField, $"{name}: error.{key} expected '{value}', got '{actual}'");
            }
        }
    }

    private static void AssertSurface(MessageProcessor processor, string surfaceId, JsonObject expected, string name)
    {
        processor.Surfaces.TryGetValue(surfaceId, out var surface);
        if (expected["exists"] is JsonValue ev && ev.GetValueKind() == JsonValueKind.False)
        {
            Assert.True(surface is null, $"{name}: surface '{surfaceId}' should not exist");
            return;
        }

        Assert.True(surface is not null, $"{name}: surface '{surfaceId}' does not exist");
        if (expected["sendDataModel"] is JsonValue sdm)
        {
            Assert.Equal(sdm.GetValue<bool>(), surface!.SendDataModel);
        }

        if (expected.ContainsKey("metadata"))
        {
            ConformanceAssert.Json(expected["metadata"], surface!.Metadata, $"{name}: metadata");
        }

        if (expected.ContainsKey("theme"))
        {
            ConformanceAssert.Json(expected["theme"], null, $"{name}: theme");
        }

        if (expected.ContainsKey("dataModel"))
        {
            ConformanceAssert.Json(expected["dataModel"], surface!.DataModel.Root, $"{name}: dataModel");
        }

        var rendered = RenderAll(surface!);
        if (expected["components"] is { } components)
        {
            var items = components switch
            {
                JsonObject map => map.Select(p => (Id: p.Key, Expected: p.Value as JsonObject)).ToList(),
                JsonArray list => list.OfType<JsonObject>().Select(o => (Id: Str(o["id"])!, Expected: (JsonObject?)o)).ToList(),
                _ => [],
            };
            foreach (var (id, componentExpected) in items)
            {
                var hasRaw = surface!.Components.TryGetValue(id, out var raw);
                rendered.TryGetValue(id, out var node);
                Assert.True(hasRaw || node is not null, $"{name}: component '{id}' missing");
                if (componentExpected is null)
                {
                    continue;
                }

                var component = node?.Component ?? raw!;
                if (Str(componentExpected["component"]) is { } type)
                {
                    Assert.Equal(type, component.Type);
                }

                if (componentExpected["@metadata"] is { } metadata)
                {
                    ConformanceAssert.Json(metadata, component.Metadata, $"{name}: {id}.@metadata");
                }

                var context = node?.Context ?? surface.RootContext;
                foreach (var (prop, value) in componentExpected)
                {
                    if (prop is "id" or "component" or "@metadata")
                    {
                        continue;
                    }

                    var actual = surface.Evaluate(component.Properties[prop], context, out _);
                    ConformanceAssert.PropertyValue(value, actual, $"{name}: {id}.{prop}");
                }
            }

            if (components is JsonArray && items.All(i => surface!.Components.ContainsKey(i.Id)))
            {
                Assert.True(items.Count == surface!.Components.Count, $"{name}: expected {items.Count} components, got {surface.Components.Count}");
            }
        }

        ConformanceAssert.ValidationFields(expected, surface!, rendered, name);
    }

    internal sealed record RenderedNode(Model.ComponentInstance Component, EvaluationContext Context);

    /// <summary>Walks the tree from the root, evaluating every property like a renderer would.</summary>
    internal static Dictionary<string, RenderedNode> RenderAll(Surface surface)
    {
        var rendered = new Dictionary<string, RenderedNode>(StringComparer.Ordinal);
        if (surface.Root is not { } root)
        {
            return rendered;
        }

        void Visit(Model.ComponentInstance component, EvaluationContext context, int depth)
        {
            if (depth > 64)
            {
                return;
            }

            var key = component.Id + string.Concat(context.Indices.Select(i => "_" + i));
            if (!rendered.TryAdd(key, new RenderedNode(component, context)))
            {
                return;
            }

            foreach (var (_, value) in component.Properties)
            {
                surface.Evaluate(value, context, out _);
            }

            if (component.Checks is not null)
            {
                surface.EvaluateChecks(component, context);
            }

            foreach (var (prop, kind) in component.Definition.ChildProperties)
            {
                if (kind == ChildReferenceKind.NestedSingle)
                {
                    foreach (var item in (component.Properties[prop] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (Str(item[component.Definition.NestedChildKeys.GetValueOrDefault(prop, "child")]) is { } childId && surface.TryGetChild(component, childId, out var nested))
                        {
                            Visit(nested, context, depth + 1);
                        }
                    }

                    continue;
                }

                foreach (var (child, childContext) in surface.ResolveChildren(component, prop, context))
                {
                    Visit(child, childContext, depth + 1);
                }
            }
        }

        Visit(root, surface.RootContext, 0);
        return rendered;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
