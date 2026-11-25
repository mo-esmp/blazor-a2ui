using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Expressions;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs the vendored A2UI conformance cases that apply to a v1.0 renderer core.
/// </summary>
public sealed class ConformanceTests
{
    /// <summary>
    /// Reads a string value, or null when the node is not a string.
    /// </summary>
    private static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>
    /// Reads the protocol version a case targets, or null when it names none.
    /// </summary>
    private static string? Version(JsonObject c) => Str(c["protocolVersion"]) ?? Str(c["catalog"]?["protocolVersion"]);

    /// <summary>
    /// Whether a case applies to a v1.0 renderer: it targets v1.0, names no version, or is a v0.9 case whose behavior did not change.
    /// </summary>
    private static bool IsV10OrNeutral(JsonObject c) => Version(c) is null or "1.0" or "v0.9";

    // ------------------------------------------------------------------ data model

    /// <summary>
    /// Gets the data-model cases, as theory data.
    /// </summary>
    public static IEnumerable<object[]> DataModelCases() => ConformanceData.Names("data_model.yaml", _ => true);

    /// <summary>
    /// Proves that data-model reads, writes, deletes and observer notifications behave as the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(DataModelCases))]
    public void DataModel_ConformanceCase_MatchesSuite(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case("data_model.yaml", name);
        DataModel model = new(c["initial"]);
        List<string> notified = new();
        List<IDisposable> subscriptions = new();
        foreach (string watch in (c["watch"] as JsonArray ?? []).Select(w => w.Required().GetValue<string>()))
        {
            subscriptions.Add(model.Observe(watch, () => notified.Add(watch)));
        }

        JsonNode? lastInput = null;

        // Act and Assert: each step runs, then its expectations are checked.
        foreach (JsonObject step in c["steps"].Required().AsArray().OfType<JsonObject>())
        {
            notified.Clear();
            string? op = Str(step["op"]);
            string path = Str(step["path"]) ?? "/";
            Exception? error = null;
            try
            {
                switch (op)
                {
                    case "get":
                        bool found = model.TryGet(path, out JsonNode? value);
                        if (step["expect_absent"] is not null)
                        {
                            (!found || value is null).ShouldBeTrue($"{name}: expected {path} absent, got {value?.ToJsonString()}");
                        }

                        if (step.ContainsKey("expect"))
                        {
                            AssertJson(step["expect"], value, $"{name}: get {path}");
                        }

                        if (Str(step["expect_type"]) is { } type)
                        {
                            (type == "list" ? value is JsonArray : value is JsonObject).ShouldBeTrue($"{name}: {path} should be a {type}");
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

            error.ShouldBeNull();
            if (step["expect_notified"] is JsonArray expectedNotified)
            {
                notified.Order().ShouldBe(expectedNotified.Select(n => n.Required().GetValue<string>()).Order());
            }

            if (step["expect_values"] is JsonObject expectedValues)
            {
                foreach ((string p, JsonNode? v) in expectedValues)
                {
                    AssertJson(v, model.Get(p), $"{name}: value at {p}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ path resolution

    /// <summary>
    /// Gets the path resolution cases, as theory data.
    /// </summary>
    public static IEnumerable<object[]> DataContextCases() => ConformanceData.Names("data_context.yaml", _ => true);

    /// <summary>
    /// Proves that relative and absolute paths resolve against a context path as the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(DataContextCases))]
    public void Resolve_ConformanceCase_MatchesSuite(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case("data_context.yaml", name);
        JsonObject args = c["args"].Required().AsObject();

        // Act
        string resolved = JsonPointer.Resolve(Str(args["path"]).Required(), Str(args["contextPath"]));

        // Assert
        resolved.ShouldBe(Str(c["expect"]));
    }

    // ------------------------------------------------------------------ expression parser

    /// <summary>
    /// Gets the expression parser cases, as theory data.
    /// </summary>
    public static IEnumerable<object[]> ExpressionCases() => ConformanceData.Names("expressions.yaml", c => Str(c["action"]) != "validate");

    /// <summary>
    /// Proves that templates parse into the parts, or fail with the errors, the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(ExpressionCases))]
    public void Parse_ConformanceCase_MatchesSuite(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case("expressions.yaml", name);
        string input = Str(c["input"]).Required();

        // Act
        List<JsonNode?>? parts = null;
        Exception? error = null;
        try
        {
            parts = ExpressionParser.Parse(input);
        }
        catch (A2UIException ex)
        {
            error = ex;
        }

        // Assert
        if (c["expect_error"] is JsonObject expectedError)
        {
            AssertError(expectedError, error, name);
            return;
        }

        error.ShouldBeNull();
        AssertJson(c["expect"], new JsonArray(parts.Required().Select(ToConformanceShape).ToArray()), name);
    }

    /// <summary>
    /// Converts a parsed template part into the shape the suite uses (<c>path</c>, <c>call</c>, <c>args</c>).
    /// </summary>
    private static JsonNode? ToConformanceShape(JsonNode? part) => part switch
    {
        JsonObject o when o.ContainsKey("@path") => new JsonObject { ["path"] = o["@path"].Required().DeepClone() },
        JsonObject o when o.ContainsKey("@call") => new JsonObject
        {
            ["call"] = o["@call"].Required().DeepClone(),
            ["args"] = new JsonObject(o["args"].Required().AsObject().Select(p => KeyValuePair.Create(p.Key, ToConformanceShape(p.Value)))),
            ["returnType"] = "any",
        },
        _ => part?.DeepClone(),
    };

    // ------------------------------------------------------------------ functions

    /// <summary>
    /// Gets the function cases that apply to a v1.0 renderer, as theory data.
    /// </summary>
    public static IEnumerable<object[]> FunctionCases() =>
        ConformanceData.Names("functions.yaml", c => Str(c["action"]) == "evaluate_function" && IsV10OrNeutral(c) && Version(c) != "v0.9"
            || Str(c["action"]) == "evaluate_function" && Str(c["function"]) is "regex" or "length" or "not" && Version(c) == "v0.9");

    /// <summary>
    /// Proves that the basic catalog's functions return the values, or raise the errors, the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(FunctionCases))]
    public async Task EvaluateAsync_ConformanceCase_MatchesSuiteAsync(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case("functions.yaml", name);
        string function = Str(c["function"]).Required();
        JsonObject args = c["args"]?.DeepClone() as JsonObject ?? new JsonObject();
        JsonNode? result = null;
        Exception? error = null;

        // Act
        try
        {
            if (c["surface"] is JsonObject || c.ContainsKey("catalogId") || c["catalogs"] is not null || c["catalogPaths"] is not null)
            {
                result = await EvaluateOnSurfaceAsync(c, function, args);
            }
            else
            {
                Catalog catalog = TestCatalogs.Basic;
                JsonObject call = new() { ["@call"] = function, ["catalogId"] = catalog.CatalogId, ["args"] = args };
                A2UIEvaluator evaluator = new(new CatalogRegistry([catalog]));
                EvaluationScope scope = new(function == "openUrl" ? EvaluationMode.Action : EvaluationMode.Binding);
                result = await evaluator.EvaluateAsync(call, EvaluationContext.Detached(), scope);
            }
        }
        catch (A2UIException ex)
        {
            error = ex;
        }

        // Assert
        if (c["expectError"] is JsonObject expectedError)
        {
            AssertError(expectedError, error, name);
            return;
        }

        error.ShouldBeNull();
        JsonNode? expected = c["expect"];
        if (expected is JsonValue ev && ev.GetValueKind() is JsonValueKind.True or JsonValueKind.False && result is JsonObject vr)
        {
            // v0.9-style boolean expectations against a v1.0 ValidationResult.
            A2UIValues.ToBoolean(vr).ShouldBe(ev.GetValue<bool>());
            return;
        }

        if (expected is JsonObject expectedObject && result is JsonObject actualObject)
        {
            foreach ((string k, JsonNode? v) in expectedObject)
            {
                AssertJson(v, actualObject[k], $"{name}: {k}");
            }

            return;
        }

        AssertJson(expected, result, name);
    }

    /// <summary>
    /// Builds the catalogs a function case names, keeping only v1.0 basic catalogs.
    /// </summary>
    private static List<Catalog> LoadCatalogPaths(JsonObject c)
    {
        List<Catalog> catalogs = new();
        if (c["catalogPaths"] is not JsonArray paths)
        {
            return [TestCatalogs.Basic];
        }

        foreach (JsonNode? p in paths)
        {
            string path = p.Required().GetValue<string>();
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
                string file = Path.Combine(AppContext.BaseDirectory, "Conformance", "data", "catalogs", Path.GetFileName(path));
                catalogs.Add(Catalog.FromJson(File.ReadAllText(file)));
            }
        }

        return catalogs;
    }

    /// <summary>
    /// Evaluates a function call on a surface built from the case: its catalogs, data model and default catalog.
    /// </summary>
    private static async Task<JsonNode?> EvaluateOnSurfaceAsync(JsonObject c, string function, JsonObject args)
    {
        JsonObject? surfaceSpec = c["surface"] as JsonObject;
        List<Catalog> catalogs = LoadCatalogPaths(c);
        List<FunctionDefinition> agentFunctions = new();
        if (function == "readSessionToken" && c["catalogPaths"] is null)
        {
            agentFunctions.Add(new FunctionDefinition("readSessionToken") { AllowedCallers = AllowedCallers.AgentOnly, ReturnType = "string" });
        }

        if (agentFunctions.Count > 0)
        {
            catalogs[0] = catalogs[0].Extend(functions: agentFunctions);
        }

        MessageProcessor processor = new(catalogs);
        JsonObject create = new() { ["surfaceId"] = "main" };
        if (Str(surfaceSpec?["catalogId"]) is { } surfaceCatalog)
        {
            create["catalogId"] = surfaceCatalog;
        }

        processor.ProcessOrThrow(new JsonObject { ["version"] = "1.0", ["createSurface"] = create });
        if (surfaceSpec?["dataModel"] is JsonObject dm)
        {
            processor.Surfaces["main"].DataModel.Set("/", dm);
        }

        JsonObject call = new() { ["@call"] = function, ["args"] = args };
        if (Str(c["catalogId"]) is { } callCatalog)
        {
            call["catalogId"] = callCatalog;
        }

        Surface surface = processor.Surfaces["main"];
        return await processor.Evaluator.EvaluateAsync(call, surface.RootContext, new EvaluationScope(EvaluationMode.Binding));
    }

    /// <summary>
    /// Asserts a case's expected error; see <see cref="ConformanceAssert.Error"/>.
    /// </summary>
    private static void AssertError(JsonObject expected, Exception? actual, string name) => ConformanceAssert.Error(expected, actual, name);

    /// <summary>
    /// Asserts JSON equality; see <see cref="ConformanceAssert.Json"/>.
    /// </summary>
    private static void AssertJson(JsonNode? expected, JsonNode? actual, string because) => ConformanceAssert.Json(expected, actual, because);
}

/// <summary>
/// Catalogs shared by the conformance harnesses.
/// </summary>
internal static class TestCatalogs
{
    /// <summary>
    /// Gets the basic catalog with en-US formatting, which the suites assume.
    /// </summary>
    public static Catalog Basic { get; } = BasicCatalog.Create(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
