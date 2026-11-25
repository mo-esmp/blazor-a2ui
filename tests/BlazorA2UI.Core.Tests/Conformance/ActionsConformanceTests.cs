using System.Globalization;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs <c>actions.yaml</c>: dispatching a component action as an <c>event</c> to the agent or a local <c>functionCall</c>.
/// </summary>
public sealed class ActionsConformanceTests
{
    /// <summary>
    /// The suite file this harness runs.
    /// </summary>
    private const string File = "actions.yaml";

    /// <summary>
    /// Gets the v1.0 cases, as theory data. Cases without a <c>protocolVersion</c> default to v0.9, which this
    /// renderer does not implement; the conformance README lets a harness skip versions outside its
    /// <c>SUPPORTED_PROTOCOL_VERSIONS</c>.
    /// </summary>
    public static IEnumerable<object[]> Cases() =>
        ConformanceData.Names(File, c => Str(c["action"]) == "dispatch_action" && Str(c["protocolVersion"]) == "1.0");

    /// <summary>
    /// Proves that an action resolves, dispatches, runs locally or fails as the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DispatchActionCore_ConformanceCase_MatchesSuiteAsync(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case(File, name);
        string surfaceId = Str(c["surfaceId"]) ?? "main";
        Catalog catalog = BasicCatalog.Create().Extend(functions:
        [
            new FunctionDefinition("incrementCounter") { ReturnType = "void" }.WithImplementation(IncrementCounterAsync),
            new FunctionDefinition("failingAction") { ReturnType = "void" }.WithImplementation(_ => throw new InvalidOperationException("Action execution failed")),
        ]);
        MessageProcessor processor = new([catalog]);
        List<JsonObject> outbound = [];
        List<A2UIErrorReport> reported = [];
        processor.ErrorReported += reported.Add;
        processor.Outbound = m =>
        {
            lock (outbound)
            {
                outbound.Add(m.Message);
            }

            return ValueTask.CompletedTask;
        };
        processor.ProcessOrThrow(new JsonObject
        {
            ["version"] = "1.0",
            ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalog.CatalogId },
        });
        Surface surface = processor.Surfaces[surfaceId];
        if (c["dataModel"] is JsonObject dataModel)
        {
            surface.DataModel.Set("/", dataModel.DeepClone());
        }

        EvaluationContext context = Scope(surface, Str(c["scope"]));
        bool userActivated = c["userActivation"] is not JsonValue activation || activation.GetValue<bool>();

        // Act
        A2UIException? thrown = null;
        try
        {
            await processor.DispatchActionCoreAsync(
                surface,
                Str(c["sourceComponentId"]) ?? "test_comp",
                c["actionPayload"]?.DeepClone(),
                context,
                c["metadata"]?.DeepClone() as JsonObject,
                userActivated,
                CancellationToken.None);
        }
        catch (A2UIException ex)
        {
            thrown = ex;
            processor.ReportActionError(surface, ex);
        }

        // Assert
        List<JsonObject> actions = outbound.Where(m => m["action"] is JsonObject).Select(m => m["action"].Required().AsObject()).ToList();
        if (c["expectError"] is JsonObject expectError)
        {
            AssertError(expectError, thrown, reported, name);
        }
        else
        {
            thrown.ShouldBeNull($"{name}: unexpected error {thrown?.Code}: {thrown?.Message}");
        }

        if (c.ContainsKey("expectDispatched"))
        {
            AssertDispatched(c["expectDispatched"] as JsonObject, actions, name);
        }

        if (c["expectDataModel"] is { } expectedData)
        {
            ConformanceAssert.Json(expectedData, surface.DataModel.Snapshot(), $"{name}: data model");
        }
    }

    /// <summary>
    /// Checks the error a case expects: a category of the thrown error, or the code, surface and message of the error the surface reported.
    /// </summary>
    private static void AssertError(JsonObject expected, A2UIException? thrown, List<A2UIErrorReport> reported, string name)
    {
        if (expected.ContainsKey("category"))
        {
            ConformanceAssert.Error(expected, thrown, name);
            return;
        }

        reported.ShouldNotBeEmpty($"{name}: expected an error report");
        A2UIErrorReport report = reported[^1];
        if (Str(expected["code"]) is { } code)
        {
            report.Code.ShouldBe(code, $"{name}: {report.Message}");
        }

        if (Str(expected["surfaceId"]) is { } surfaceId)
        {
            report.SurfaceId.ShouldBe(surfaceId);
        }

        if (Str(expected["message"]) is { } message)
        {
            report.Message.ShouldContain(message);
        }
    }

    /// <summary>
    /// Checks the dispatched event against <c>expectDispatched</c>; null means nothing was dispatched.
    /// </summary>
    private static void AssertDispatched(JsonObject? expected, List<JsonObject> actions, string name)
    {
        if (expected is null)
        {
            actions.ShouldBeEmpty($"{name}: expected no dispatched event");
            return;
        }

        actions.ShouldNotBeEmpty($"{name}: expected a dispatched event");
        JsonObject actual = actions[0];
        foreach (string key in (string[])["name", "context", "userMessage", "surfaceId", "sourceComponentId", "metadata"])
        {
            if (expected.ContainsKey(key))
            {
                ConformanceAssert.Json(expected[key], actual[key], $"{name}: {key}");
            }
        }

        if (expected["timestampIso8601"] is JsonValue || expected["hasTimestamp"] is JsonValue)
        {
            DateTimeOffset.TryParse(Str(actual["timestamp"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                .ShouldBeTrue($"{name}: timestamp '{actual["timestamp"]}' is not ISO 8601");
        }
    }

    /// <summary>
    /// Builds the data scope a case names. A scope ending in a list index, such as <c>/items/1</c>, is a template item, so <c>@index</c> works there.
    /// </summary>
    private static EvaluationContext Scope(Surface surface, string? scope)
    {
        if (scope is null or "/")
        {
            return surface.RootContext;
        }

        int slash = scope.LastIndexOf('/');
        return int.TryParse(scope[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? surface.RootContext.ForTemplateItem(slash == 0 ? "/" : scope[..slash], index)
            : EvaluationContext.Detached(surface.DataModel, scope);
    }

    /// <summary>
    /// The suite's <c>incrementCounter</c> function: adds <c>step</c> (default 1) to the number at <c>path</c> (default <c>/count</c>).
    /// </summary>
    private static ValueTask<JsonNode?> IncrementCounterAsync(FunctionInvocation invocation)
    {
        string path = Str(invocation.Arg("path")) ?? "/count";
        double step = A2UIValues.ToDouble(invocation.Arg("step")) ?? 1;
        double current = A2UIValues.ToDouble(invocation.Context.Data.Get(path)) ?? 0;
        invocation.Context.Data.Set(path, JsonValue.Create(current + step));
        return ValueTask.FromResult<JsonNode?>(null);
    }

    /// <summary>
    /// Reads a string value, or null for anything else.
    /// </summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
