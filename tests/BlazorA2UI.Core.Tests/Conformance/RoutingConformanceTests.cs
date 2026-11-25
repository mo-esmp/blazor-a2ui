using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Processing;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs <c>agent_function_routing.yaml</c>: v1.0 fallback routing of function calls to the agent
/// with <c>callAgentFunction</c>, following "Writing cases for route_agent_function" in the
/// conformance README.
/// </summary>
public class RoutingConformanceTests
{
    private const string File = "agent_function_routing.yaml";

    // Re-evaluating a binding whose cancelled call should be sent again has two cases in the suite,
    // one per re-evaluation model (see the conformance README). This renderer re-evaluates on any
    // surface change, like the Python and Swift cores, and writing an equal value is not a change.
    private const string OtherReevaluationModel = "test_agent_routing_cancelled_call_is_sent_again_when_its_value_is_rewritten";

    public static IEnumerable<object[]> Cases() =>
        ConformanceData.Names(File, c => Str(c["action"]) == "route_agent_function" && Str(c["protocolVersion"]) == "1.0"
                                         && Str(c["name"]) != OtherReevaluationModel);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Route(string name)
    {
        var c = ConformanceData.Case(File, name);
        var catalogs = (c["catalogs"] as JsonArray ?? []).OfType<JsonObject>().Select(BuildCatalog).ToList();
        var processor = new MessageProcessor(catalogs);
        var outbound = new List<JsonObject>();
        var errors = new List<A2UIErrorReport>();
        processor.ErrorReported += errors.Add;
        var surfaceSpec = c["surface"] as JsonObject ?? new JsonObject();
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

        var create = new JsonObject { ["surfaceId"] = "main" };
        if (Str(surfaceSpec["catalogId"]) is { } defaultCatalog)
        {
            create["catalogId"] = defaultCatalog;
        }

        processor.ProcessOrThrow(new JsonObject { ["version"] = "1.0", ["createSurface"] = create });
        var surface = processor.Surfaces["main"];
        if (c["dataModel"] is JsonObject dm)
        {
            surface.DataModel.Set("/", dm.DeepClone());
        }

        var sentCalls = new List<string>();
        var evaluated = new List<A2UIBinding>();
        surface.Changed += () =>
        {
            // Like a renderer: every binding re-evaluates when the surface changes.
            foreach (var binding in evaluated.ToList())
            {
                binding.Refresh();
            }
        };
        var stepIndex = 0;
        foreach (var step in (c["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var label = $"{name} step {stepIndex++}";
            outbound.Clear();
            errors.Clear();
            var sentBefore = sentCalls.Count;

            if (step["evaluate"] is { } call)
            {
                var mode = Str(step["as"]) ?? "binding";
                switch (mode)
                {
                    case "binding":
                    case "check":
                        var binding = new A2UIBinding(surface, call.DeepClone(), surface.RootContext, mode == "check" ? EvaluationMode.Check : EvaluationMode.Binding);
                        evaluated.Add(binding);
                        binding.Refresh();
                        break;
                    case "action":
                        await Dispatch(processor, surface, new JsonObject { ["functionCall"] = call.DeepClone() });
                        break;
                    case "event":
                        await Dispatch(processor, surface, new JsonObject { ["event"] = call.DeepClone() });
                        break;
                }
            }
            else if (step["respond"] is JsonObject respond)
            {
                var index = (int)A2UIValues.ToDouble(respond["call"])!.Value;
                var body = new JsonObject { ["functionCallId"] = sentCalls[index] };
                if (respond.ContainsKey("error"))
                {
                    body["error"] = respond["error"]!.DeepClone();
                }
                else
                {
                    body["value"] = respond["value"]?.DeepClone();
                }

                processor.Process(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = body });
            }
            else if (step["updateData"] is JsonObject update)
            {
                surface.DataModel.Set(Str(update["path"])!, update["value"]?.DeepClone());
            }
            else if (step.ContainsKey("cancelSurfaceCalls"))
            {
                surface.AgentCalls.CancelAll("Cancelled by the test.");
            }
            else if (step.ContainsKey("deleteSurface"))
            {
                processor.Process(new JsonObject { ["version"] = "1.0", ["deleteSurface"] = new JsonObject { ["surfaceId"] = "main" } });
            }

            await Settle(processor);

            List<JsonObject> messages;
            lock (outbound)
            {
                messages = [.. outbound];
            }

            var calls = messages.Where(m => m["callAgentFunction"] is JsonObject).Select(m => m["callAgentFunction"]!.AsObject()).ToList();
            foreach (var sent in calls)
            {
                Assert.Equal("main", Str(sent["surfaceId"]));
                sentCalls.Add(Str(sent["functionCallId"])!);
            }

            var expectedOutbound = step["expectOutbound"] as JsonArray ?? [];
            var actualPayloads = calls.Select(m => m["callFunction"]).ToList();
            Assert.True(expectedOutbound.Count == actualPayloads.Count,
                $"{label}: expected {expectedOutbound.Count} outbound calls, got {actualPayloads.Count}: {string.Join(", ", actualPayloads.Select(p => p?.ToJsonString()))}");
            if (step["outboundInAnyOrder"] is JsonValue anyOrder && anyOrder.GetValueKind() == JsonValueKind.True)
            {
                var remaining = actualPayloads.ToList();
                foreach (var expected in expectedOutbound)
                {
                    var match = remaining.FindIndex(a => JsonNode.DeepEquals(Normalize(expected), Normalize(a)));
                    Assert.True(match >= 0, $"{label}: no outbound call matches {expected?.ToJsonString()}");
                    remaining.RemoveAt(match);
                }
            }
            else
            {
                for (var i = 0; i < expectedOutbound.Count; i++)
                {
                    ConformanceAssert.Json(expectedOutbound[i], actualPayloads[i], $"{label}: outbound {i}");
                }
            }

            var expectedErrors = step["expectErrors"] as JsonArray ?? [];
            Assert.True(expectedErrors.Count == errors.Count,
                $"{label}: expected {expectedErrors.Count} errors, got {errors.Count}: {string.Join("; ", errors.Select(e => e.Code + " " + e.Message))}");
            for (var i = 0; i < expectedErrors.Count; i++)
            {
                var expected = expectedErrors[i]!.AsObject();
                var code = Str(expected["code"]);
                var actualCode = errors[i].Code;
                Assert.True(code == actualCode || code == "CATALOG_ERROR" && actualCode == A2UIErrorCodes.CatalogError,
                    $"{label}: error {i} expected {code}, got {actualCode}: {errors[i].Message}");
                if (Str(expected["message"]) is { } message)
                {
                    Assert.True(errors[i].Message.Contains(message, StringComparison.Ordinal), $"{label}: error {i} expected '{message}' in '{errors[i].Message}'");
                }
            }

            var actions = messages.Where(m => m["action"] is JsonObject).Select(m => m["action"]!.AsObject()).ToList();
            var expectedActions = step["expectActions"] as JsonArray ?? [];
            Assert.True(expectedActions.Count == actions.Count, $"{label}: expected {expectedActions.Count} actions, got {actions.Count}");
            for (var i = 0; i < expectedActions.Count; i++)
            {
                var expected = expectedActions[i]!.AsObject();
                Assert.Equal(Str(expected["name"]), Str(actions[i]["name"]));
                ConformanceAssert.Json(expected["context"] ?? new JsonObject(), actions[i]["context"] ?? new JsonObject(), $"{label}: action {i} context");
                if (expected.ContainsKey("userMessage"))
                {
                    ConformanceAssert.Json(expected["userMessage"], actions[i]["userMessage"], $"{label}: action {i} userMessage");
                }
            }

            if (step["expectValues"] is JsonArray expectedValues)
            {
                Assert.True(expectedValues.Count == evaluated.Count, $"{label}: expected {expectedValues.Count} values, {evaluated.Count} were evaluated");
                for (var i = 0; i < evaluated.Count; i++)
                {
                    ConformanceAssert.Json(expectedValues[i], Value(evaluated[i]), $"{label}: value {i}");
                }
            }
        }
    }

    private static JsonNode? Value(A2UIBinding binding)
    {
        if (binding.Mode == EvaluationMode.Check)
        {
            return binding.IsPending || binding.Errors.Count == 0 && A2UIValues.ToValidationResult(binding.Value, null)["valid"]!.GetValue<bool>();
        }

        return binding.Value;
    }

    private static async Task Dispatch(MessageProcessor processor, Surface surface, JsonObject action)
    {
        var definition = new ComponentDefinition("Button") { Properties = new HashSet<string> { "action" } };
        var component = new ComponentInstance(
            "trigger",
            "Button",
            surface.TryGetCatalog(surface.CatalogId ?? string.Empty, out var catalog) ? catalog : processor.Catalogs.Catalogs.First(),
            definition,
            new JsonObject { ["action"] = action },
            null,
            null,
            null,
            null);
        var task = processor.DispatchActionAsync(surface, component, action, surface.RootContext, CancellationToken.None);
        await Task.WhenAny(task, Task.Delay(50));
    }

    private static async Task Settle(MessageProcessor processor)
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Yield();
            await processor.WhenIdleAsync();
            await Task.Delay(1);
        }
    }

    private static Catalog BuildCatalog(JsonObject spec)
    {
        var functions = new List<FunctionDefinition>();
        foreach (var local in spec["localFunctions"] as JsonArray ?? [])
        {
            functions.Add(TestCatalogs.Basic.Functions[local!.GetValue<string>()]);
        }

        foreach (var (fname, declNode) in spec["agentFunctions"] as JsonObject ?? [])
        {
            var decl = declNode as JsonObject ?? new JsonObject();
            functions.Add(new FunctionDefinition(fname)
            {
                ReturnType = Str(decl["returnType"]) ?? "any",
                AllowedCallers = FunctionDefinition.ParseCallers(Str(decl["allowedCallers"])),
                RequiresUserActivation = decl["requiresUserActivation"] is JsonValue r && r.GetValueKind() == JsonValueKind.True,
                ArgsSchema = decl["args"]?.DeepClone() as JsonObject,
            });
        }

        return new Catalog(Str(spec["catalogId"])!, [], functions);
    }

    private static JsonNode? Normalize(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
