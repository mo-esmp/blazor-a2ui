using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Expressions;
using BlazorA2UI.Processing;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>Runs the vendored A2UI conformance cases that apply to a v1.0 renderer core.</summary>
public class ConformanceTests
{
    private static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string? Version(JsonObject c) => Str(c["protocolVersion"]) ?? Str(c["catalog"]?["protocolVersion"]);

    private static bool IsV10OrNeutral(JsonObject c) => Version(c) is null or "1.0" or "v0.9";

    // ------------------------------------------------------------------ data model

    public static IEnumerable<object[]> DataModelCases() => ConformanceData.Names("data_model.yaml", _ => true);

    [Theory]
    [MemberData(nameof(DataModelCases))]
    public void DataModel(string name)
    {
        var c = ConformanceData.Case("data_model.yaml", name);
        var model = new DataModel(c["initial"]);
        var notified = new List<string>();
        var subscriptions = new List<IDisposable>();
        foreach (var watch in (c["watch"] as JsonArray ?? []).Select(w => w!.GetValue<string>()))
        {
            subscriptions.Add(model.Observe(watch, () => notified.Add(watch)));
        }

        JsonNode? lastInput = null;
        foreach (var step in c["steps"]!.AsArray().OfType<JsonObject>())
        {
            notified.Clear();
            var op = Str(step["op"]);
            var path = Str(step["path"]) ?? "/";
            Exception? error = null;
            try
            {
                switch (op)
                {
                    case "get":
                        var found = model.TryGet(path, out var value);
                        if (step["expect_absent"] is not null)
                        {
                            Assert.True(!found || value is null, $"{name}: expected {path} absent, got {value?.ToJsonString()}");
                        }

                        if (step.ContainsKey("expect"))
                        {
                            AssertJson(step["expect"], value, $"{name}: get {path}");
                        }

                        if (Str(step["expect_type"]) is { } type)
                        {
                            Assert.True(type == "list" ? value is JsonArray : value is JsonObject, $"{name}: {path} should be a {type}");
                        }

                        break;
                    case "set":
                        lastInput = step["value"]?.DeepClone();
                        model.Set(path, lastInput);
                        break;
                    case "delete":
                        model.Delete(path);
                        break;
                    case "dispose":
                        model.DisposeObservers();
                        break;
                    case "mutate_input":
                        if (lastInput is JsonObject input)
                        {
                            input[path.TrimStart('/')] = step["value"]?.DeepClone();
                        }

                        break;
                }
            }
            catch (A2UIException ex)
            {
                error = ex;
            }

            if (step["expect_error"] is JsonObject expectedError)
            {
                AssertError(expectedError, error, name);
                continue;
            }

            Assert.Null(error);
            if (step["expect_notified"] is JsonArray expectedNotified)
            {
                Assert.Equal(expectedNotified.Select(n => n!.GetValue<string>()).Order(), notified.Order());
            }

            if (step["expect_values"] is JsonObject expectedValues)
            {
                foreach (var (p, v) in expectedValues)
                {
                    AssertJson(v, model.Get(p), $"{name}: value at {p}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ path resolution

    public static IEnumerable<object[]> DataContextCases() => ConformanceData.Names("data_context.yaml", _ => true);

    [Theory]
    [MemberData(nameof(DataContextCases))]
    public void ResolvePath(string name)
    {
        var c = ConformanceData.Case("data_context.yaml", name);
        var args = c["args"]!.AsObject();
        Assert.Equal(Str(c["expect"]), JsonPointer.Resolve(Str(args["path"])!, Str(args["contextPath"])));
    }

    // ------------------------------------------------------------------ expression parser

    public static IEnumerable<object[]> ExpressionCases() => ConformanceData.Names("expressions.yaml", c => Str(c["action"]) != "validate");

    [Theory]
    [MemberData(nameof(ExpressionCases))]
    public void ParseExpression(string name)
    {
        var c = ConformanceData.Case("expressions.yaml", name);
        var input = Str(c["input"])!;
        if (c["expect_error"] is JsonObject expectedError)
        {
            Exception? error = null;
            try
            {
                ExpressionParser.Parse(input);
            }
            catch (A2UIException ex)
            {
                error = ex;
            }

            AssertError(expectedError, error, name);
            return;
        }

        var parts = ExpressionParser.Parse(input);
        AssertJson(c["expect"], new JsonArray(parts.Select(ToConformanceShape).ToArray()), name);
    }

    private static JsonNode? ToConformanceShape(JsonNode? part) => part switch
    {
        JsonObject o when o.ContainsKey("@path") => new JsonObject { ["path"] = o["@path"]!.DeepClone() },
        JsonObject o when o.ContainsKey("@call") => new JsonObject
        {
            ["call"] = o["@call"]!.DeepClone(),
            ["args"] = new JsonObject(o["args"]!.AsObject().Select(p => KeyValuePair.Create(p.Key, ToConformanceShape(p.Value)))),
            ["returnType"] = "any",
        },
        _ => part?.DeepClone(),
    };

    // ------------------------------------------------------------------ functions

    public static IEnumerable<object[]> FunctionCases() =>
        ConformanceData.Names("functions.yaml", c => Str(c["action"]) == "evaluate_function" && IsV10OrNeutral(c) && Version(c) != "v0.9"
            || Str(c["action"]) == "evaluate_function" && Str(c["function"]) is "regex" or "length" or "not" && Version(c) == "v0.9");

    [Theory]
    [MemberData(nameof(FunctionCases))]
    public async Task EvaluateFunction(string name)
    {
        var c = ConformanceData.Case("functions.yaml", name);
        var function = Str(c["function"])!;
        var args = c["args"]?.DeepClone() as JsonObject ?? new JsonObject();
        JsonNode? result = null;
        Exception? error = null;
        try
        {
            if (c["surface"] is JsonObject || c.ContainsKey("catalogId") || c["catalogs"] is not null || c["catalogPaths"] is not null)
            {
                result = await EvaluateOnSurface(c, function, args);
            }
            else
            {
                var catalog = TestCatalogs.Basic;
                var call = new JsonObject { ["@call"] = function, ["catalogId"] = catalog.CatalogId, ["args"] = args };
                var evaluator = new A2UIEvaluator(new CatalogRegistry([catalog]));
                var scope = new EvaluationScope(function == "openUrl" ? EvaluationMode.Action : EvaluationMode.Binding);
                result = await evaluator.EvaluateAsync(call, EvaluationContext.Detached(), scope);
            }
        }
        catch (A2UIException ex)
        {
            error = ex;
        }

        if (c["expectError"] is JsonObject expectedError)
        {
            AssertError(expectedError, error, name);
            return;
        }

        Assert.Null(error);
        var expected = c["expect"];
        if (expected is JsonValue ev && ev.GetValueKind() is JsonValueKind.True or JsonValueKind.False && result is JsonObject vr)
        {
            // v0.9-style boolean expectations against a v1.0 ValidationResult.
            Assert.Equal(ev.GetValue<bool>(), A2UIValues.ToBoolean(vr));
            return;
        }

        if (expected is JsonObject expectedObject && result is JsonObject actualObject)
        {
            foreach (var (k, v) in expectedObject)
            {
                AssertJson(v, actualObject[k], $"{name}: {k}");
            }

            return;
        }

        AssertJson(expected, result, name);
    }

    private static List<Catalog> LoadCatalogPaths(JsonObject c)
    {
        var catalogs = new List<Catalog>();
        if (c["catalogPaths"] is not JsonArray paths)
        {
            return [TestCatalogs.Basic];
        }

        foreach (var p in paths)
        {
            var path = p!.GetValue<string>();
            if (path.StartsWith("catalogs/basic/", StringComparison.Ordinal))
            {
                if (!path.StartsWith("catalogs/basic/v1/", StringComparison.Ordinal))
                {
                    continue; // Only v1.0 catalogs are available to a v1.0 surface.
                }

                catalogs.Add(TestCatalogs.Basic);
            }
            else
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Conformance", "data", "catalogs", Path.GetFileName(path));
                catalogs.Add(Catalog.FromJson(File.ReadAllText(file)));
            }
        }

        return catalogs;
    }

    private static async Task<JsonNode?> EvaluateOnSurface(JsonObject c, string function, JsonObject args)
    {
        var surfaceSpec = c["surface"] as JsonObject;
        var catalogs = LoadCatalogPaths(c);
        var agentFunctions = new List<FunctionDefinition>();
        if (function == "readSessionToken" && c["catalogPaths"] is null)
        {
            agentFunctions.Add(new FunctionDefinition("readSessionToken") { AllowedCallers = AllowedCallers.AgentOnly, ReturnType = "string" });
        }

        if (agentFunctions.Count > 0)
        {
            catalogs[0] = catalogs[0].Extend(functions: agentFunctions);
        }

        var processor = new MessageProcessor(catalogs);
        var create = new JsonObject { ["surfaceId"] = "main" };
        if (Str(surfaceSpec?["catalogId"]) is { } surfaceCatalog)
        {
            create["catalogId"] = surfaceCatalog;
        }

        processor.ProcessOrThrow(new JsonObject { ["version"] = "1.0", ["createSurface"] = create });
        if (surfaceSpec?["dataModel"] is JsonObject dm)
        {
            processor.Surfaces["main"].DataModel.Set("/", dm);
        }

        var call = new JsonObject { ["@call"] = function, ["args"] = args };
        if (Str(c["catalogId"]) is { } callCatalog)
        {
            call["catalogId"] = callCatalog;
        }

        var surface = processor.Surfaces["main"];
        return await processor.Evaluator.EvaluateAsync(call, surface.RootContext, new EvaluationScope(EvaluationMode.Binding));
    }

    private static void AssertError(JsonObject expected, Exception? actual, string name) => ConformanceAssert.Error(expected, actual, name);

    private static void AssertJson(JsonNode? expected, JsonNode? actual, string because) => ConformanceAssert.Json(expected, actual, because);
}

internal static class TestCatalogs
{
    public static Catalog Basic { get; } = BasicCatalog.Create(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
