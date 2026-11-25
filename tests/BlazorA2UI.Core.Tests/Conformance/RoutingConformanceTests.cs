using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs <c>agent_function_routing.yaml</c>: v1.0 fallback routing of function calls to the agent
/// with <c>callAgentFunction</c>, following "Writing cases for route_agent_function" in the
/// conformance README.
/// </summary>
public sealed class RoutingConformanceTests
{
    /// <summary>
    /// The suite file this harness runs.
    /// </summary>
    private const string File = "agent_function_routing.yaml";

    // Re-evaluating a binding whose cancelled call should be sent again has two cases in the suite,
    // one per re-evaluation model (see the conformance README). This renderer re-evaluates on any
    // surface change, like the Python and Swift cores, and writing an equal value is not a change.
    /// <summary>
    /// The one routing case written for the other re-evaluation model; see the comment above.
    /// </summary>
    private const string OtherReevaluationModel = "test_agent_routing_cancelled_call_is_sent_again_when_its_value_is_rewritten";

    /// <summary>
    /// Gets the v1.0 routing cases, as theory data.
    /// </summary>
    public static IEnumerable<object[]> Cases() =>
        ConformanceData.Names(File, c => Str(c["action"]) == "route_agent_function" && Str(c["protocolVersion"]) == "1.0"
                                         && Str(c["name"]) != OtherReevaluationModel);

    /// <summary>
    /// Proves that function calls are routed to the agent, answered, cancelled and reported as the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RouteAgentFunction_ConformanceCase_MatchesSuiteAsync(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case(File, name);
        List<Catalog> catalogs = (c["catalogs"] as JsonArray ?? []).OfType<JsonObject>().Select(BuildCatalog).ToList();
        MessageProcessor processor = new(catalogs);
        List<JsonObject> outbound = new();
        List<A2UIErrorReport> errors = new();
        processor.ErrorReported += errors.Add;
        JsonObject surfaceSpec = c["surface"] as JsonObject ?? new JsonObject();
        if (surfaceSpec["outboundListener"] is not JsonValue ol || ol.GetValueKind() != JsonValueKind.False)
        {
            processor.Outbound = m =>
            {
                lock (outbound)
                {
                    outbound.Add(m.Message);
                }

                return ValueTask.CompletedTask;
            };
        }

        JsonObject create = new() { ["surfaceId"] = "main" };
        if (Str(surfaceSpec["catalogId"]) is { } defaultCatalog)
        {
            create["catalogId"] = defaultCatalog;
        }

        processor.ProcessOrThrow(new JsonObject { ["version"] = "1.0", ["createSurface"] = create });
        Surface surface = processor.Surfaces["main"];
        if (c["dataModel"] is JsonObject dm)
        {
            surface.DataModel.Set("/", dm.DeepClone());
        }

        List<string> sentCalls = new();
        List<A2UIBinding> evaluated = new();
        surface.Changed += () =>
        {
            // Like a renderer: every binding re-evaluates when the surface changes.
            foreach (A2UIBinding? binding in evaluated.ToList())
            {
                binding.Refresh();
            }
        };
        int stepIndex = 0;
        // Act and Assert: each step runs, then its outbound calls, errors, actions and values are checked.
        foreach (JsonObject step in (c["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string label = $"{name} step {stepIndex++}";
            outbound.Clear();
            errors.Clear();
            int sentBefore = sentCalls.Count;

            if (step["evaluate"] is { } call)
            {
                string mode = Str(step["as"]) ?? "binding";
                switch (mode)
                {
                    case "binding":
                    case "check":
                        A2UIBinding binding = new(surface, call.DeepClone(), surface.RootContext, mode == "check" ? EvaluationMode.Check : EvaluationMode.Binding);
                        evaluated.Add(binding);
                        binding.Refresh();
                        break;
                    case "action":
                        await DispatchAsync(processor, surface, new JsonObject { ["functionCall"] = call.DeepClone() });
                        break;
                    case "event":
                        await DispatchAsync(processor, surface, new JsonObject { ["event"] = call.DeepClone() });
                        break;
                }
            }
            else if (step["respond"] is JsonObject respond)
            {
                int index = (int)A2UIValues.ToDouble(respond["call"]).Required();
                JsonObject body = new() { ["functionCallId"] = sentCalls[index] };
                if (respond.ContainsKey("error"))
                {
                    body["error"] = respond["error"].Required().DeepClone();
                }
                else
                {
                    body["value"] = respond["value"]?.DeepClone();
                }

                processor.Process(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = body });
            }
            else if (step["updateData"] is JsonObject update)
            {
                surface.DataModel.Set(Str(update["path"]).Required(), update["value"]?.DeepClone());
            }
            else if (step.ContainsKey("cancelSurfaceCalls"))
            {
                surface.AgentCalls.CancelAll("Cancelled by the test.");
            }
            else if (step.ContainsKey("deleteSurface"))
            {
                processor.Process(new JsonObject { ["version"] = "1.0", ["deleteSurface"] = new JsonObject { ["surfaceId"] = "main" } });
            }

            await SettleAsync(processor);

            List<JsonObject> messages;
            lock (outbound)
            {
                messages = [.. outbound];
            }

            List<JsonObject> calls = messages.Where(m => m["callAgentFunction"] is JsonObject).Select(m => m["callAgentFunction"].Required().AsObject()).ToList();
            foreach (JsonObject? sent in calls)
            {
                Str(sent["surfaceId"]).ShouldBe("main");
                sentCalls.Add(Str(sent["functionCallId"]).Required());
            }

            JsonArray expectedOutbound = step["expectOutbound"] as JsonArray ?? [];
            List<JsonNode?> actualPayloads = calls.Select(m => m["callFunction"]).ToList();
            actualPayloads.Count.ShouldBe(expectedOutbound.Count, $"{label}: expected {expectedOutbound.Count} outbound calls, got {actualPayloads.Count}: {string.Join(", ", actualPayloads.Select(p => p?.ToJsonString()))}");
            if (step["outboundInAnyOrder"] is JsonValue anyOrder && anyOrder.GetValueKind() == JsonValueKind.True)
            {
                List<JsonNode?> remaining = actualPayloads.ToList();
                foreach (JsonNode? expected in expectedOutbound)
                {
                    int match = remaining.FindIndex(a => JsonNode.DeepEquals(Normalize(expected), Normalize(a)));
                    (match >= 0).ShouldBeTrue($"{label}: no outbound call matches {expected?.ToJsonString()}");
                    remaining.RemoveAt(match);
                }
            }
            else
            {
                for (int i = 0; i < expectedOutbound.Count; i++)
                {
                    ConformanceAssert.Json(expectedOutbound[i], actualPayloads[i], $"{label}: outbound {i}");
                }
            }

            JsonArray expectedErrors = step["expectErrors"] as JsonArray ?? [];
            errors.Count.ShouldBe(expectedErrors.Count, $"{label}: expected {expectedErrors.Count} errors, got {errors.Count}: {string.Join("; ", errors.Select(e => e.Code + " " + e.Message))}");
            for (int i = 0; i < expectedErrors.Count; i++)
            {
                JsonObject expected = expectedErrors[i].Required().AsObject();
                string? code = Str(expected["code"]);
                string actualCode = errors[i].Code;
                (code == actualCode || code == "CATALOG_ERROR" && actualCode == A2UIErrorCodes.CatalogError).ShouldBeTrue($"{label}: error {i} expected {code}, got {actualCode}: {errors[i].Message}");
                if (Str(expected["message"]) is { } message)
                {
                    errors[i].Message.Contains(message, StringComparison.Ordinal).ShouldBeTrue($"{label}: error {i} expected '{message}' in '{errors[i].Message}'");
                }
            }

            List<JsonObject> actions = messages.Where(m => m["action"] is JsonObject).Select(m => m["action"].Required().AsObject()).ToList();
            JsonArray expectedActions = step["expectActions"] as JsonArray ?? [];
            actions.Count.ShouldBe(expectedActions.Count, $"{label}: expected {expectedActions.Count} actions, got {actions.Count}");
            for (int i = 0; i < expectedActions.Count; i++)
            {
                JsonObject expected = expectedActions[i].Required().AsObject();
                Str(actions[i]["name"]).ShouldBe(Str(expected["name"]));
                ConformanceAssert.Json(expected["context"] ?? new JsonObject(), actions[i]["context"] ?? new JsonObject(), $"{label}: action {i} context");
                if (expected.ContainsKey("userMessage"))
                {
                    ConformanceAssert.Json(expected["userMessage"], actions[i]["userMessage"], $"{label}: action {i} userMessage");
                }
            }

            if (step["expectValues"] is JsonArray expectedValues)
            {
                evaluated.Count.ShouldBe(expectedValues.Count, $"{label}: expected {expectedValues.Count} values, {evaluated.Count} were evaluated");
                for (int i = 0; i < evaluated.Count; i++)
                {
                    ConformanceAssert.Json(expectedValues[i], Value(evaluated[i]), $"{label}: value {i}");
                }
            }
        }
    }

    /// <summary>
    /// Returns what a binding shows: its value, or for a check, whether it currently passes.
    /// </summary>
    private static JsonNode? Value(A2UIBinding binding)
    {
        if (binding.Mode == EvaluationMode.Check)
        {
            return binding.IsPending || binding.Errors.Count == 0 && A2UIValues.ToValidationResult(binding.Value, null)["valid"].Required().GetValue<bool>();
        }

        return binding.Value;
    }

    /// <summary>
    /// Dispatches an action the way a button would, waiting briefly so the action can send its calls.
    /// </summary>
    private static async Task DispatchAsync(MessageProcessor processor, Surface surface, JsonObject action)
    {
        ComponentDefinition definition = new("Button") { Properties = new HashSet<string> { "action" } };
        ComponentInstance component = new(
            "trigger",
            "Button",
            surface.TryGetCatalog(surface.CatalogId ?? string.Empty, out Catalog? catalog) ? catalog : processor.Catalogs.Catalogs.First(),
            definition,
            new JsonObject { ["action"] = action },
            null,
            null,
            null,
            null);
        Task task = processor.DispatchActionAsync(surface, component, action, surface.RootContext, metadata: null, CancellationToken.None);
        await Task.WhenAny(task, Task.Delay(50));
    }

    /// <summary>
    /// Lets pending callbacks and outbound sends run before the next step.
    /// </summary>
    private static async Task SettleAsync(MessageProcessor processor)
    {
        for (int i = 0; i < 5; i++)
        {
            await Task.Yield();
            await processor.WhenIdleAsync();
            await Task.Delay(1);
        }
    }

    /// <summary>
    /// Builds a catalog from a case's catalog spec: basic functions it names locally, plus agent-side function definitions.
    /// </summary>
    private static Catalog BuildCatalog(JsonObject spec)
    {
        List<FunctionDefinition> functions = new();
        foreach (JsonNode? local in spec["localFunctions"] as JsonArray ?? [])
        {
            functions.Add(TestCatalogs.Basic.Functions[local.Required().GetValue<string>()]);
        }

        foreach ((string fname, JsonNode? declNode) in spec["agentFunctions"] as JsonObject ?? [])
        {
            JsonObject decl = declNode as JsonObject ?? new JsonObject();
            functions.Add(new FunctionDefinition(fname)
            {
                ReturnType = Str(decl["returnType"]) ?? "any",
                AllowedCallers = FunctionDefinition.ParseCallers(Str(decl["allowedCallers"])),
                RequiresUserActivation = decl["requiresUserActivation"] is JsonValue r && r.GetValueKind() == JsonValueKind.True,
                ArgsSchema = decl["args"]?.DeepClone() as JsonObject,
            });
        }

        return new Catalog(Str(spec["catalogId"]).Required(), [], functions);
    }

    /// <summary>
    /// Returns a copy of a JSON value with its numbers in the form the parser produces, for comparison.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    /// <summary>
    /// Reads a string value, or null when the node is not a string.
    /// </summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
