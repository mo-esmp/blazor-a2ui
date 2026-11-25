using System.Globalization;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Processing;
using BlazorA2UI.Rpc;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Runs <c>rpc_functions.yaml</c>: agent-initiated <c>callRendererFunction</c>, renderer-initiated
/// <c>callAgentFunction</c> with its ids, timeouts, cancellation and disposal, and agent calls made from
/// bindings, checks and actions. The mock functions follow the reference harness
/// (<c>python/a2ui_core/tests/test_conformance.py</c>).
/// </summary>
public sealed class RpcConformanceTests
{
    /// <summary>
    /// The suite file this harness runs.
    /// </summary>
    private const string File = "rpc_functions.yaml";

    /// <summary>
    /// Gets every case, as theory data.
    /// </summary>
    public static IEnumerable<object[]> Cases() =>
        ConformanceData.Names(File, c => Str(c["action"]) == "handle_rpc");

    /// <summary>
    /// Proves that renderer and agent function calls are answered, correlated, refused and settled as the suite expects.
    /// </summary>
    /// <param name="name">The case name.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HandleRpc_ConformanceCase_MatchesSuiteAsync(string name)
    {
        // Arrange
        JsonObject c = ConformanceData.Case(File, name);
        JsonObject args = c["args"] as JsonObject ?? [];
        JsonObject expect = c["expect"] as JsonObject ?? [];
        JsonObject? message = args["message"] as JsonObject;
        JsonObject? outboundCall = args["outboundCall"] as JsonObject;
        (string catalogId, string version) = CatalogInfo(c, args, message, outboundCall);
        List<Catalog> catalogs = BuildCatalogs(catalogId, version, args["functionMetadata"] as JsonObject ?? []);
        MessageProcessor processor = new(catalogs, new A2UIProcessorOptions { AgentCallTimeout = Timeout.InfiniteTimeSpan });
        List<JsonObject> outbound = [];
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        if (args["outboundListener"] is not JsonValue listener || listener.GetValue<bool>())
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

        if (Str(args["surfaceId"]) is { } setupSurface)
        {
            CreateSurface(processor, setupSurface, catalogId);
            if (args["dataModel"] is JsonObject dataModel)
            {
                processor.Surfaces[setupSurface].DataModel.Set("/", dataModel.DeepClone());
            }
        }

        // Act and Assert: each scenario the case describes runs and is checked.
        bool handled = false;
        if (message is not null)
        {
            await InboundAsync(c, message, expect, args, processor, outbound, errors);
            handled = true;
        }

        if (outboundCall is not null)
        {
            CreateSurface(processor, Str(outboundCall["surfaceId"]).Required(), catalogId);
            await OutboundAsync(c, args, outboundCall, expect, processor, outbound, errors);
            handled = true;
        }

        if (args["uiBindingScenario"] is JsonObject bindingScenario)
        {
            await BindingScenarioAsync(bindingScenario, expect, processor, outbound, errors);
            handled = true;
        }

        if (args["uiActionFallbackScenario"] is JsonObject actionScenario)
        {
            await ActionScenarioAsync(actionScenario, expect, processor, outbound, errors);
            handled = true;
        }

        handled.ShouldBeTrue($"{name}: no scenario matched");
    }

    /// <summary>
    /// Processes an inbound message and checks the <c>rendererFunctionResponse</c> it produces, or the error it raises.
    /// </summary>
    private static async Task InboundAsync(
        JsonObject c, JsonObject message, JsonObject expect, JsonObject args, MessageProcessor processor, List<JsonObject> outbound, List<A2UIErrorReport> errors)
    {
        string name = Str(c["name"]).Required();
        bool userActivated = args["userActivationPresent"] is JsonValue activation && activation.GetValue<bool>();
        JsonObject? expectError = c["expectError"] as JsonObject ?? expect["error"] as JsonObject;
        A2UIException? thrown = null;
        try
        {
            processor.ProcessOrThrow(message.DeepClone(), new A2UIExecutionContext { IsUserActivated = userActivated });
        }
        catch (A2UIException ex)
        {
            thrown = ex;
        }

        await processor.WhenIdleAsync();
        if (expectError is not null)
        {
            thrown.ShouldNotBeNull($"{name}: expected an error");
            JsonObject detail = expect["error"] as JsonObject ?? expectError;
            if (Str(detail["code"]) is { } code)
            {
                thrown.Code.ShouldBe(code, $"{name}: {thrown.Message}");
            }

            if (Str(detail["functionCallId"]) is { } functionCallId)
            {
                thrown.FunctionCallId.ShouldBe(functionCallId);
            }

            if (Str(detail["message"]) is { } fragment)
            {
                thrown.Message.ShouldContain(fragment);
            }

            if (c["expectError"] is JsonObject category && category.ContainsKey("category"))
            {
                ConformanceAssert.Error(new JsonObject { ["category"] = category["category"]?.DeepClone() }, thrown, name);
            }

            return;
        }

        thrown.ShouldBeNull($"{name}: unexpected error {thrown?.Code}: {thrown?.Message}");
        List<JsonObject> responses = outbound.Where(m => m["rendererFunctionResponse"] is JsonObject).ToList();
        if (expect["response"] is not JsonObject expectedResponse)
        {
            responses.ShouldBeEmpty($"{name}: expected no response");
            errors.ShouldBeEmpty($"{name}: expected no error");
            processor.IsAgentCallPending(Str(message["agentFunctionResponse"]?["functionCallId"]) ?? string.Empty).ShouldBeFalse();
            return;
        }

        responses.Count.ShouldBe(1, $"{name}: expected one response");
        JsonObject actual = responses[0];
        Str(actual["version"]).ShouldBe(Str(expectedResponse["version"]));
        JsonObject actualBody = actual["rendererFunctionResponse"].Required().AsObject();
        JsonObject expectedBody = expectedResponse["rendererFunctionResponse"].Required().AsObject();
        Str(actualBody["functionCallId"]).ShouldBe(Str(expectedBody["functionCallId"]));
        if (expectedBody.ContainsKey("value"))
        {
            actualBody.ContainsKey("error").ShouldBeFalse($"{name}: unexpected error {actualBody["error"]?.ToJsonString()}");
            ConformanceAssert.Json(expectedBody["value"], actualBody["value"], $"{name}: value");
        }

        if (expectedBody["error"] is JsonObject expectedError)
        {
            JsonObject actualError = actualBody["error"].Required().AsObject();
            Str(actualError["code"]).ShouldBe(Str(expectedError["code"]), $"{name}: {actualError["message"]}");
            if (Str(expectedError["message"]) is { } fragment)
            {
                Str(actualError["message"]).Required().ShouldContain(fragment);
            }
        }
    }

    /// <summary>
    /// Makes a renderer-initiated agent call and checks how it settles: answered, refused, timed out, cancelled or disposed.
    /// </summary>
    private static async Task OutboundAsync(
        JsonObject c, JsonObject args, JsonObject outboundCall, JsonObject expect, MessageProcessor processor, List<JsonObject> outbound, List<A2UIErrorReport> errors)
    {
        string name = Str(c["name"]).Required();
        JsonObject? expectError = c["expectError"] as JsonObject ?? expect["error"] as JsonObject;
        if (expectError is null)
        {
            if (Str(expect["correlatedCallId"]) is { } correlated)
            {
                Str(args["inboundResponse"]?["agentFunctionResponse"]?["functionCallId"]).ShouldBe(correlated);
            }

            Task<JsonNode?> call = Start(processor, outboundCall);
            processor.ProcessOrThrow(args["inboundResponse"]?.DeepClone());
            call.IsCompleted.ShouldBeTrue($"{name}: the call did not settle");
            ConformanceAssert.Json(expect["result"], await call, $"{name}: result");
            return;
        }

        string? expectedCode = Str(expectError["code"]);
        if (args["outboundListener"] is JsonValue listener && !listener.GetValue<bool>())
        {
            (await CaptureAsync(() => Start(processor, outboundCall))).Code.ShouldBe(expectedCode ?? A2UIErrorCodes.NoListener);
        }
        else if (args["secondOutboundCall"] is JsonObject second)
        {
            Task<JsonNode?> first = Start(processor, outboundCall);
            (await CaptureAsync(() => Start(processor, second))).Code.ShouldBe(expectedCode ?? A2UIErrorCodes.Duplicate);
            processor.Surfaces[Str(outboundCall["surfaceId"]).Required()].AgentCalls.CancelAll("Test finished.");
            await CaptureAsync(() => first);
        }
        else if (Str(args["afterCall"]) == "cancel")
        {
            Task<JsonNode?> pending = Start(processor, outboundCall);
            processor.Surfaces[Str(outboundCall["surfaceId"]).Required()].AgentCalls.CancelAll("Cancelled by the test.");
            (await CaptureAsync(() => pending)).Code.ShouldBe(expectedCode ?? A2UIErrorCodes.Cancelled);
        }
        else if (Str(args["afterCall"]) == "dispose")
        {
            Task<JsonNode?> pending = Start(processor, outboundCall);
            processor.Dispose();
            (await CaptureAsync(() => pending)).Code.ShouldBe(Str(expect["inFlightError"]?["code"]) ?? A2UIErrorCodes.Cancelled);
            (await CaptureAsync(() => Start(processor, outboundCall))).Code.ShouldBe(expectedCode ?? A2UIErrorCodes.Disposed);
            AssertLateResponseIgnored(args, expect, processor, outbound, errors);
        }
        else if (outboundCall.ContainsKey("timeoutMs"))
        {
            (await CaptureAsync(() => Start(processor, outboundCall))).Code.ShouldBe(expectedCode ?? A2UIErrorCodes.Timeout);
            AssertLateResponseIgnored(args, expect, processor, outbound, errors);
        }
        else
        {
            throw new InvalidOperationException($"{name}: unhandled outbound error scenario.");
        }
    }

    /// <summary>
    /// Processes a late <c>agentFunctionResponse</c> and checks that it changes nothing: no message, no error, no pending call.
    /// </summary>
    private static void AssertLateResponseIgnored(JsonObject args, JsonObject expect, MessageProcessor processor, List<JsonObject> outbound, List<A2UIErrorReport> errors)
    {
        if (args["lateInboundResponse"] is not JsonObject late)
        {
            return;
        }

        int outboundBefore = outbound.Count;
        int errorsBefore = errors.Count;
        processor.Process(late.DeepClone());
        if (expect["lateResponseIgnored"] is JsonValue ignored && ignored.GetValue<bool>())
        {
            outbound.Count.ShouldBe(outboundBefore);
            errors.Count.ShouldBe(errorsBefore);
            processor.IsAgentCallPending(Str(late["agentFunctionResponse"]?["functionCallId"]).Required()).ShouldBeFalse();
        }
    }

    /// <summary>
    /// Renders a component whose binding or check calls an agent function, answers the call, and checks the value, siblings, check result and reported error.
    /// </summary>
    private static async Task BindingScenarioAsync(
        JsonObject scenario, JsonObject expect, MessageProcessor processor, List<JsonObject> outbound, List<A2UIErrorReport> errors)
    {
        string surfaceId = Str(scenario["surfaceId"]).Required();
        string catalogId = Str(scenario["catalogId"]).Required();
        processor.AddCatalog(new Catalog(catalogId, BasicCatalog.Create().Components.Values, LocalFunctions(scenario["localFunctions"] as JsonObject)));
        CreateSurface(processor, surfaceId, catalogId);
        Surface surface = processor.Surfaces[surfaceId];
        JsonArray components = scenario["components"] is JsonArray list ? (JsonArray)list.DeepClone() : [scenario["component"]?.DeepClone()];
        processor.ProcessOrThrow(new JsonObject
        {
            ["version"] = "1.0",
            ["updateComponents"] = new JsonObject { ["surfaceId"] = surfaceId, ["components"] = components },
        });
        string observedId = Str(scenario["observe"]?["componentId"]) ?? "root";
        string property = Str(scenario["observe"]?["property"]) ?? "text";
        ComponentInstance observed = surface.Components[observedId];
        A2UIBinding binding = new(surface, observed.Properties[property]?.DeepClone(), surface.RootContext);
        binding.Refresh();
        surface.EvaluateChecks(observed, surface.RootContext);
        await processor.WhenIdleAsync();
        if (expect.ContainsKey("initialValue"))
        {
            ConformanceAssert.Json(expect["initialValue"], binding.Value, "initial value");
        }

        string callId = AssertOutboundCall(outbound, expect["outboundCall"].Required().AsObject());
        processor.ProcessOrThrow(Response(scenario, callId));
        await Task.Delay(20);
        binding.Refresh();
        CheckOutcome checks = surface.EvaluateChecks(observed, surface.RootContext);
        if (expect.ContainsKey("resolvedValue"))
        {
            ConformanceAssert.Json(expect["resolvedValue"], binding.Value, "resolved value");
        }

        if (expect["siblings"] is JsonObject siblings)
        {
            foreach ((string siblingId, JsonNode? props) in siblings)
            {
                ComponentInstance sibling = surface.Components[siblingId];
                foreach ((string key, JsonNode? value) in props.Required().AsObject())
                {
                    ConformanceAssert.Json(value, surface.Evaluate(sibling.Properties[key], surface.RootContext, out _), $"sibling {siblingId}.{key}");
                }
            }
        }

        if (expect["validationResult"] is JsonObject validation)
        {
            if (validation["valid"] is JsonValue valid)
            {
                checks.IsValid.ShouldBe(valid.GetValue<bool>());
            }

            if (Str(validation["message"]) is { } text)
            {
                Str(checks.Failures[0]["message"]).ShouldBe(text);
            }

            if (Str(validation["severity"]) is { } severity)
            {
                Str(checks.Failures[0]["severity"]).ShouldBe(severity);
            }
        }

        AssertSurfaceError(errors, expect["surfaceError"] as JsonObject, callId);
    }

    /// <summary>
    /// Runs an action whose <c>functionCall</c> falls back to the agent, answers the call, and checks the result, data model and reported error.
    /// </summary>
    private static async Task ActionScenarioAsync(
        JsonObject scenario, JsonObject expect, MessageProcessor processor, List<JsonObject> outbound, List<A2UIErrorReport> errors)
    {
        string surfaceId = Str(scenario["surfaceId"]).Required();
        string catalogId = Str(scenario["catalogId"]).Required();
        processor.AddCatalog(new Catalog(catalogId, [], LocalFunctions(scenario["localFunctions"] as JsonObject)));
        CreateSurface(processor, surfaceId, catalogId);
        Surface surface = processor.Surfaces[surfaceId];
        Task<JsonNode?> action = processor.DispatchActionCoreAsync(
            surface, "test_comp", scenario["actionPayload"]?.DeepClone(), surface.RootContext, null, userActivated: true, CancellationToken.None);
        await processor.WhenIdleAsync();
        string callId = AssertOutboundCall(outbound, expect["outboundCall"].Required().AsObject());
        processor.ProcessOrThrow(Response(scenario, callId));
        JsonNode? result = null;
        try
        {
            result = await action;
        }
        catch (A2UIException ex)
        {
            processor.ReportActionError(surface, ex);
        }

        if (expect.ContainsKey("result"))
        {
            ConformanceAssert.Json(expect["result"], result, "result");
        }

        if (expect["dataModel"] is { } dataModel)
        {
            ConformanceAssert.Json(dataModel, surface.DataModel.Snapshot(), "data model");
        }

        AssertSurfaceError(errors, expect["surfaceError"] as JsonObject, callId);
    }

    /// <summary>
    /// Checks that exactly one <c>callAgentFunction</c> was sent, with the expected surface and call, and returns its id.
    /// </summary>
    private static string AssertOutboundCall(List<JsonObject> outbound, JsonObject expected)
    {
        List<JsonObject> calls = outbound.Where(m => m["callAgentFunction"] is JsonObject).ToList();
        calls.Count.ShouldBe(1, "expected one callAgentFunction message");
        Str(calls[0]["version"]).ShouldBe(Str(expected["version"]));
        JsonObject actual = calls[0]["callAgentFunction"].Required().AsObject();
        JsonObject expectedCall = expected["callAgentFunction"].Required().AsObject();
        Str(actual["surfaceId"]).ShouldBe(Str(expectedCall["surfaceId"]));
        ConformanceAssert.Json(expectedCall["callFunction"], actual["callFunction"], "callFunction");
        return Str(actual["functionCallId"]).Required();
    }

    /// <summary>
    /// Checks the first error the surface reported against the expected code and function call id (<c>$outboundCallId</c> is the call that was sent).
    /// </summary>
    private static void AssertSurfaceError(List<A2UIErrorReport> errors, JsonObject? expected, string callId)
    {
        if (expected is null)
        {
            return;
        }

        errors.ShouldNotBeEmpty("expected a surface error");
        if (Str(expected["code"]) is { } code)
        {
            errors[0].Code.ShouldBe(code, errors[0].Message);
        }

        if (Str(expected["functionCallId"]) is { } functionCallId)
        {
            errors[0].FunctionCallId.ShouldBe(functionCallId == "$outboundCallId" ? callId : functionCallId);
        }
    }

    /// <summary>
    /// Builds the agent's <c>agentFunctionResponse</c> to a scenario's call: its error, or its value.
    /// </summary>
    private static JsonObject Response(JsonObject scenario, string callId)
    {
        JsonObject body = new() { ["functionCallId"] = callId };
        if (scenario.ContainsKey("inboundResponseError"))
        {
            body["error"] = scenario["inboundResponseError"]?.DeepClone();
        }
        else
        {
            body["value"] = scenario["inboundResponseValue"]?.DeepClone();
        }

        return new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = body };
    }

    /// <summary>
    /// Starts a renderer-initiated agent call described by an <c>outboundCall</c>.
    /// </summary>
    private static Task<JsonNode?> Start(MessageProcessor processor, JsonObject spec)
    {
        JsonObject call = spec["callFunction"].Required().AsObject();
        TimeSpan? timeout = A2UIValues.ToDouble(spec["timeoutMs"]) is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        return processor.CallAgentFunctionAsync(
            Str(spec["surfaceId"]).Required(),
            Str(call["catalogId"]) ?? BasicCatalog.CatalogId,
            Str(call["@call"]).Required(),
            call["args"] as JsonObject,
            new A2UIAgentCallOptions { FunctionCallId = Str(spec["functionCallId"]), Timeout = timeout });
    }

    /// <summary>
    /// Runs an operation that must fail and returns its A2UI error, whether it throws at once or from its task.
    /// </summary>
    /// <exception cref="ShouldAssertException">The operation succeeded.</exception>
    private static async Task<A2UIException> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (A2UIException ex)
        {
            return ex;
        }

        throw new ShouldAssertException("Expected the operation to fail with an A2UI error.");
    }

    /// <summary>
    /// Creates a surface on the catalog unless it already exists.
    /// </summary>
    private static void CreateSurface(MessageProcessor processor, string surfaceId, string catalogId)
    {
        if (processor.Surfaces.ContainsKey(surfaceId))
        {
            return;
        }

        processor.ProcessOrThrow(new JsonObject
        {
            ["version"] = "1.0",
            ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalogId },
        });
    }

    /// <summary>
    /// Works out the case's primary catalog id and protocol version the way the reference harness does.
    /// </summary>
    private static (string CatalogId, string Version) CatalogInfo(JsonObject c, JsonObject args, JsonObject? message, JsonObject? outboundCall)
    {
        string? catalogId = Str(args["catalogId"]);
        if (catalogId is null && message?["callRendererFunction"] is JsonObject call)
        {
            string expectedMessage = Str(c["expect"]?["response"]?["rendererFunctionResponse"]?["error"]?["message"]) ?? string.Empty;
            if (!expectedMessage.Contains("Catalog not found", StringComparison.Ordinal))
            {
                catalogId = Str(call["callFunction"]?["catalogId"]);
            }
        }

        catalogId ??= Str(outboundCall?["callFunction"]?["catalogId"]);
        string version = Str(args["catalogVersion"]) ?? Str(c["catalog"]?["protocolVersion"]) ?? Str(c["protocolVersion"]) ?? "1.0";
        return (catalogId ?? "basic", version.TrimStart('v'));
    }

    /// <summary>
    /// Builds the catalogs of a case: the declared mock functions grouped by catalog, with the basic functions added to the <c>basic</c> catalog, which always exists.
    /// </summary>
    private static List<Catalog> BuildCatalogs(string primaryId, string version, JsonObject metadata)
    {
        Dictionary<string, List<FunctionDefinition>> byCatalog = new(StringComparer.Ordinal) { [primaryId] = [] };
        foreach ((string functionName, JsonNode? node) in metadata)
        {
            JsonObject meta = node.Required().AsObject();
            FunctionDefinition definition = new FunctionDefinition(functionName)
            {
                AllowedCallers = FunctionDefinition.ParseCallers(Str(meta["allowedCallers"]) ?? "rendererOrAgent"),
                RequiresUserActivation = meta["requiresUserActivation"] is JsonValue activation && activation.GetValue<bool>(),
                ArgsSchema = (meta["schema"] ?? meta["parameters"])?.DeepClone() as JsonObject,
            }.WithImplementation(invocation => MockAsync(functionName, invocation));
            string target = Str(meta["catalogId"]) ?? primaryId;
            if (!byCatalog.TryGetValue(target, out List<FunctionDefinition>? functions))
            {
                functions = [];
                byCatalog[target] = functions;
            }

            functions.Add(definition);
        }

        byCatalog.TryAdd("basic", []);
        List<Catalog> catalogs = [];
        foreach ((string id, List<FunctionDefinition> functions) in byCatalog)
        {
            if (id == "basic")
            {
                HashSet<string> declared = functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                functions.AddRange(BasicCatalog.Create().Functions.Values.Where(f => !declared.Contains(f.Name)));
            }

            catalogs.Add(new Catalog(id, [], functions, version));
        }

        return catalogs;
    }

    /// <summary>
    /// The reference harness's mock renderer functions.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <c>failingFunction</c> mock always fails.</exception>
    private static async ValueTask<JsonNode?> MockAsync(string name, FunctionInvocation invocation)
    {
        switch (name)
        {
            case "playMedia":
                return new JsonObject { ["playing"] = true, ["timestamp"] = 0 };
            case "openExternalUrl":
                return new JsonObject { ["opened"] = true };
            case "failingFunction":
                throw new InvalidOperationException("An error occurred during function execution.");
            case "calculateTax":
                return JsonValue.Create((A2UIValues.ToDouble(invocation.Arg("amount")) ?? 0) * 0.1);
            case "echoArgs":
                return invocation.Args.DeepClone();
            case "renderTemplate":
                FunctionDefinition formatString = BasicCatalog.Create().Functions["formatString"];
                A2UIFunction implementation = formatString.Implementation.Required();
                return await implementation(invocation);
            case "tagValue":
                return string.Create(CultureInfo.InvariantCulture, $"tagged:{A2UIValues.ToDisplayString(invocation.Arg("value"))}");
            case "secretRendererOnly":
                return "SECRET";
            case "agentSecret":
                return "AGENT_OK";
            default:
                return null;
        }
    }

    /// <summary>
    /// Builds a scenario's local functions: each returns null and, with an <c>effect.setData</c>, first writes that value.
    /// </summary>
    private static List<FunctionDefinition> LocalFunctions(JsonObject? spec)
    {
        List<FunctionDefinition> functions = [];
        foreach ((string functionName, JsonNode? node) in spec ?? [])
        {
            JsonObject? setData = node?["effect"]?["setData"] as JsonObject;
            functions.Add(new FunctionDefinition(functionName) { ReturnType = Str(node?["returnType"]) ?? "any" }.WithImplementation(invocation =>
            {
                if (Str(setData?["path"]) is { } path)
                {
                    invocation.Context.Data.Set(path, setData?["value"]?.DeepClone());
                }

                return ValueTask.FromResult<JsonNode?>(null);
            }));
        }

        return functions;
    }

    /// <summary>
    /// Reads a string value, or null for anything else.
    /// </summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
