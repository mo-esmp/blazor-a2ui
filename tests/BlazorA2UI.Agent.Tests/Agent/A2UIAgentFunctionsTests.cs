using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIAgentFunctions"/>, through the <c>callAgentFunction</c> messages that
/// <see cref="A2UIRendererMessageProcessor"/> answers.
/// </summary>
public sealed class A2UIAgentFunctionsTests
{
    /// <summary>
    /// The id of the test catalog.
    /// </summary>
    private const string ToolsId = "urn:tools";

    /// <summary>
    /// Proves that a valid call runs its handler with the schema's defaults applied and the call's context, and is
    /// answered with the handler's value.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ValidCall_RunsHandlerWithDefaults()
    {
        // Arrange
        A2UIAgentFunctionCall? received = null;
        A2UIAgentFunctions functions = new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", (call, _) =>
        {
            received = call;
            return ValueTask.FromResult<JsonNode?>(new JsonObject { ["found"] = call.Args["key"]!.GetValue<string>() });
        });
        A2UIRendererMessageProcessor processor = new(functions);

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k1"}}"""));

        // Assert
        result.Handled.ShouldBeTrue();
        result.Replies.ShouldHaveSingleItem().ToJsonString().ShouldBe(
            """{"version":"1.0","agentFunctionResponse":{"functionCallId":"c1","value":{"found":"k1"}}}""");
        received.ShouldNotBeNull();
        received.Args.ToJsonString().ShouldBe("""{"key":"k1","limit":5}""");
        (received.CatalogId, received.Name, received.SurfaceId, received.FunctionCallId).ShouldBe((ToolsId, "lookup", "main", "c1"));
    }

    /// <summary>
    /// Proves that each call the agent cannot run is answered with the spec's code and never reaches a handler.
    /// </summary>
    /// <param name="callFunction">The <c>callFunction</c> object, as JSON.</param>
    /// <param name="surfaceId">The call's surface id.</param>
    /// <param name="code">The expected error code.</param>
    [Theory]
    [InlineData("""{"@call":"lookup","catalogId":"urn:other","args":{"key":"k"}}""", "main", A2UIErrorCodes.UnknownFunction)]
    [InlineData("""{"@call":"missing","catalogId":"urn:tools"}""", "main", A2UIErrorCodes.UnknownFunction)]
    [InlineData("""{"@call":"@index","catalogId":"urn:tools"}""", "main", A2UIErrorCodes.UnknownFunction)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools"}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":1}}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k","extra":1}}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools","args":"k"}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"},"x":1}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"","catalogId":"urn:tools"}""", "main", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}""", "", A2UIErrorCodes.InvalidFunctionCall)]
    public async Task ProcessAsync_CallAgentCannotRun_AnswersWithCode(string callFunction, string surfaceId, string code)
    {
        // Arrange
        int runs = 0;
        A2UIRendererMessageProcessor processor = new(new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", (_, _) =>
        {
            runs++;
            return ValueTask.FromResult<JsonNode?>(null);
        }));

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall(callFunction, surfaceId));

        // Assert
        JsonNode reply = result.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!;
        reply["functionCallId"]!.GetValue<string>().ShouldBe("c1");
        reply["error"]!["code"]!.GetValue<string>().ShouldBe(code);
        runs.ShouldBe(0);
    }

    /// <summary>
    /// Proves that a call without a function call id cannot be answered and is rejected.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoFunctionCallId_Throws()
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new();
        JsonObject message = JsonNode.Parse("""{"version":"1.0","callAgentFunction":{"surfaceId":"main","callFunction":{"@call":"x"}}}""")!.AsObject();

        // Act
        Func<Task> process = () => processor.ProcessAsync(message);

        // Assert
        await process.ShouldThrowAsync<A2UIValidationException>();
    }

    /// <summary>
    /// Proves that a processor without agent functions answers every call with <c>UNKNOWN_FUNCTION</c>.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoRegistry_AnswersUnknownFunction()
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new();

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools"}"""));

        // Assert
        result.Handled.ShouldBeTrue();
        result.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["error"]!["code"]!.GetValue<string>().ShouldBe(A2UIErrorCodes.UnknownFunction);
    }

    /// <summary>
    /// Proves that a handler's <see cref="A2UIException"/> chooses the code and message of the answer.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_HandlerThrowsA2UIException_KeepsCodeAndMessage()
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new(new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", (_, _) =>
            throw new A2UIException("NOT_FOUND", "No such key.")));

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}"""));

        // Assert
        result.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["error"]!.ToJsonString().ShouldBe("""{"code":"NOT_FOUND","message":"No such key."}""");
    }

    /// <summary>
    /// Proves that any other exception is answered with a generic message, so its details never reach the renderer,
    /// and is passed to <see cref="A2UIAgentFunctions.FunctionFailed"/> for logging.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_HandlerThrows_HidesDetailsAndRaisesEvent()
    {
        // Arrange
        Exception? logged = null;
        A2UIAgentFunctions functions = new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", (_, _) =>
            throw new InvalidOperationException("Connection string Server=db;Password=secret failed."));
        functions.FunctionFailed += (_, ex) => logged = ex;
        A2UIRendererMessageProcessor processor = new(functions);

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}"""));

        // Assert
        string reply = result.Replies.ShouldHaveSingleItem().ToJsonString();
        reply.ShouldContain(A2UIErrorCodes.ExecutionError);
        reply.ShouldNotContain("secret");
        logged.ShouldBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that a handler that runs past the timeout is answered with <c>EXECUTION_ERROR</c> and its token is
    /// cancelled.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_HandlerTooSlow_TimesOut()
    {
        // Arrange
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A2UIAgentFunctions functions = new A2UIAgentFunctions([ToolsCatalog()]) { Timeout = TimeSpan.FromMilliseconds(50) }
            .Add(ToolsId, "lookup", async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult(token.IsCancellationRequested);
                    throw;
                }

                return null;
            });
        A2UIRendererMessageProcessor processor = new(functions);

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}"""));

        // Assert
        result.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["error"]!["code"]!.GetValue<string>().ShouldBe(A2UIErrorCodes.ExecutionError);
        (await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    /// <summary>
    /// Proves that cancelling processing cancels the handler and answers nothing.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_CallerCancels_ThrowsWithoutReply()
    {
        // Arrange
        using CancellationTokenSource source = new();
        A2UIRendererMessageProcessor processor = new(new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", async (_, token) =>
        {
            await source.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }));

        // Act
        Func<Task> process = () => processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}"""), source.Token);

        // Assert
        await process.ShouldThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Proves that a call without a catalog id runs the only agent function of that name, and is refused when several
    /// catalogs have one.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoCatalogId_UsesOnlyMatch()
    {
        // Arrange
        static ValueTask<JsonNode?> Handler(A2UIAgentFunctionCall call, CancellationToken token) => ValueTask.FromResult<JsonNode?>(call.CatalogId);
        A2UIRendererMessageProcessor single = new(new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", Handler));
        A2UIRendererMessageProcessor ambiguous = new(new A2UIAgentFunctions([ToolsCatalog(), ToolsCatalog("urn:more")])
            .Add(ToolsId, "lookup", Handler)
            .Add("urn:more", "lookup", Handler));
        JsonObject call = AgentCall("""{"@call":"lookup","args":{"key":"k"}}""");

        // Act
        A2UIRendererMessageResult found = await single.ProcessAsync(call);
        A2UIRendererMessageResult refused = await ambiguous.ProcessAsync(call);

        // Assert
        found.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["value"]!.GetValue<string>().ShouldBe(ToolsId);
        refused.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["error"]!["code"]!.GetValue<string>().ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
    }

    /// <summary>
    /// Proves that a value JSON cannot carry is answered with an error instead of a message that fails to serialize.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ValueNotFinite_AnswersExecutionError()
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new(new A2UIAgentFunctions([ToolsCatalog()]).Add(ToolsId, "lookup", (_, _) =>
            ValueTask.FromResult<JsonNode?>(JsonValue.Create(double.NaN))));

        // Act
        A2UIRendererMessageResult result = await processor.ProcessAsync(AgentCall("""{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k"}}"""));

        // Assert
        result.Replies.ShouldHaveSingleItem()["agentFunctionResponse"]!["error"]!["code"]!.GetValue<string>().ShouldBe(A2UIErrorCodes.ExecutionError);
    }

    /// <summary>
    /// Proves that a handler is refused for a function a renderer would never send to the agent.
    /// </summary>
    [Fact]
    public void Add_FunctionRendererNeverSends_Throws()
    {
        // Arrange
        A2UIAgentFunctions functions = new([ToolsCatalog(), BasicCatalog.Create()]);
        static ValueTask<JsonNode?> Handler(A2UIAgentFunctionCall call, CancellationToken token) => ValueTask.FromResult<JsonNode?>(null);
        functions.Add(ToolsId, "lookup", Handler);

        // Act
        Action unknownCatalog = () => functions.Add("urn:other", "lookup", Handler);
        Action undeclared = () => functions.Add(ToolsId, "missing", Handler);
        Action implemented = () => functions.Add(A2UIProtocol.BasicCatalogId, "formatString", Handler);
        Action agentOnly = () => functions.Add(ToolsId, "notify", Handler);
        Action twice = () => functions.Add(ToolsId, "lookup", Handler);

        // Assert
        unknownCatalog.ShouldThrow<ArgumentException>();
        undeclared.ShouldThrow<ArgumentException>();
        implemented.ShouldThrow<ArgumentException>();
        agentOnly.ShouldThrow<ArgumentException>();
        twice.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves the whole round trip: a renderer's call to an agent function reaches the handler, and the renderer's
    /// pending call settles with the agent's answer.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_RendererCallsAgent_RendererGetsValue()
    {
        // Arrange
        List<JsonObject> sent = [];
        Catalog catalog = ToolsCatalog();
        using MessageProcessor renderer = new([catalog])
        {
            Outbound = message =>
            {
                sent.Add(message.Message);
                return ValueTask.CompletedTask;
            },
        };
        renderer.ProcessOrThrow(A2UIMessages.CreateSurface("main", catalogId: ToolsId));
        A2UIRendererMessageProcessor agent = new(new A2UIAgentFunctions([catalog]).Add(ToolsId, "lookup", (call, _) =>
            ValueTask.FromResult<JsonNode?>($"{call.Args["key"]}:{call.Args["limit"]}")));
        Task<JsonNode?> value = renderer.CallAgentFunctionAsync("main", ToolsId, "lookup", new JsonObject { ["key"] = "k1" });

        // Act
        A2UIRendererMessageResult result = await agent.ProcessAsync(sent.ShouldHaveSingleItem());
        renderer.ProcessOrThrow(result.Replies.ShouldHaveSingleItem());

        // Assert
        (await value.WaitAsync(TimeSpan.FromSeconds(5)))!.GetValue<string>().ShouldBe("k1:5");
    }

    /// <summary>
    /// Builds a catalog that declares agent functions: <c>lookup</c>, which renderers may call, and <c>notify</c>,
    /// which only the agent may call.
    /// </summary>
    /// <param name="catalogId">The catalog id.</param>
    internal static Catalog ToolsCatalog(string catalogId = ToolsId)
    {
        JsonObject schema = JsonNode.Parse(
            """{"type":"object","properties":{"key":{"type":"string"},"limit":{"type":"number","default":5}},"required":["key"],"additionalProperties":false}""")!.AsObject();
        FunctionDefinition[] functions =
        [
            new FunctionDefinition("lookup") { ArgsSchema = schema },
            new FunctionDefinition("notify") { AllowedCallers = AllowedCallers.AgentOnly },
        ];
        return new Catalog(catalogId, [], functions, A2UIProtocol.Version);
    }

    /// <summary>
    /// Builds a <c>callAgentFunction</c> message with the id <c>c1</c>.
    /// </summary>
    /// <param name="callFunction">The <c>callFunction</c> object, as JSON.</param>
    /// <param name="surfaceId">The surface id.</param>
    private static JsonObject AgentCall(string callFunction, string surfaceId = "main") =>
        JsonNode.Parse($$$"""{"version":"1.0","callAgentFunction":{"surfaceId":"{{{surfaceId}}}","functionCallId":"c1","callFunction":{{{callFunction}}}}}""")!.AsObject();
}
