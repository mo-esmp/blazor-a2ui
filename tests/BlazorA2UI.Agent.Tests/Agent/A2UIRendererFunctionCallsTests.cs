using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIRendererFunctionCalls"/>.
/// </summary>
public sealed class A2UIRendererFunctionCallsTests
{
    /// <summary>
    /// The id of the test catalog.
    /// </summary>
    private const string ToolsId = "urn:tools";

    /// <summary>
    /// Proves that a call builds the spec's <c>callRendererFunction</c> message and waits for its response.
    /// </summary>
    [Fact]
    public void Call_Valid_BuildsMessage()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UICall call = Echo(A2UIValue.Path("/name"));

        // Act
        A2UIRendererFunctionCall pending = calls.Call(call, "main", functionCallId: "c1");

        // Assert
        pending.FunctionCallId.ShouldBe("c1");
        pending.Message.ToJsonString().ShouldBe(
            """{"version":"1.0","callRendererFunction":{"surfaceId":"main","functionCallId":"c1","callFunction":{"@call":"echo","args":{"value":{"@path":"/name"}},"catalogId":"urn:tools"}}}""");
        pending.Result.IsCompleted.ShouldBeFalse();
        calls.IsPending("c1").ShouldBeTrue();
        calls.PendingCount.ShouldBe(1);
    }

    /// <summary>
    /// Proves that a value response settles the call once, and that a repeated response is not taken.
    /// </summary>
    [Fact]
    public async Task Resolve_Value_SettlesCallOnce()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), functionCallId: "c1");
        JsonObject response = JsonNode.Parse("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c1","value":{"ok":true}}}""")!.AsObject();

        // Act
        bool first = calls.Resolve(response);
        bool second = calls.Resolve(response);

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse();
        (await pending.Result)!.ToJsonString().ShouldBe("""{"ok":true}""");
        calls.PendingCount.ShouldBe(0);
    }

    /// <summary>
    /// Proves that an error response fails the call with the renderer's code.
    /// </summary>
    [Fact]
    public async Task Resolve_Error_FailsWithRendererCode()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), functionCallId: "c1");

        // Act
        bool taken = calls.Resolve(new A2UIFunctionResponse("c1", null, new A2UIFunctionError("PROVIDER_NOT_FOUND", "No provider.")));

        // Assert
        taken.ShouldBeTrue();
        A2UIException error = await Should.ThrowAsync<A2UIException>(pending.Result);
        error.Code.ShouldBe("PROVIDER_NOT_FOUND");
        error.FunctionCallId.ShouldBe("c1");
    }

    /// <summary>
    /// Proves that a malformed response to a pending call fails it at once with <c>UNKNOWN_ERROR</c>, and that a
    /// response to no pending call, or another message, is not taken and never throws.
    /// </summary>
    [Fact]
    public async Task Resolve_MalformedOrUnknown_FailsPendingOrDeclines()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), functionCallId: "c1");

        // Act
        bool unknown = calls.Resolve(JsonNode.Parse("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"other","value":1}}"""));
        bool notResponse = calls.Resolve(JsonNode.Parse("""{"version":"1.0","error":{"code":"X","message":"m","functionCallId":"c1"}}"""));
        bool notMessage = calls.Resolve(JsonNode.Parse("""[1]"""));
        bool malformed = calls.Resolve(JsonNode.Parse("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c1"}}"""));

        // Assert
        (unknown, notResponse, notMessage, malformed).ShouldBe((false, false, false, true));
        (await Should.ThrowAsync<A2UIException>(pending.Result)).Code.ShouldBe(A2UIErrorCodes.UnknownError);
    }

    /// <summary>
    /// Proves that a call without a response times out, and a late response is ignored.
    /// </summary>
    [Fact]
    public async Task Call_NoResponse_TimesOut()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), timeout: TimeSpan.FromMilliseconds(20), functionCallId: "c1");

        // Act
        A2UIException error = await Should.ThrowAsync<A2UIException>(pending.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        bool late = calls.Resolve(new A2UIFunctionResponse("c1", 1, null));

        // Assert
        error.Code.ShouldBe(A2UIErrorCodes.Timeout);
        late.ShouldBeFalse();
        calls.PendingCount.ShouldBe(0);
    }

    /// <summary>
    /// Proves that cancelling a call fails it with <c>CANCELLED</c> and forgets it.
    /// </summary>
    [Fact]
    public async Task Call_Cancelled_FailsAndForgets()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        using CancellationTokenSource source = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), functionCallId: "c1", cancellationToken: source.Token);

        // Act
        await source.CancelAsync();

        // Assert
        (await Should.ThrowAsync<A2UIException>(pending.Result)).Code.ShouldBe(A2UIErrorCodes.Cancelled);
        calls.IsPending("c1").ShouldBeFalse();
    }

    /// <summary>
    /// Proves that disposing fails every pending call and refuses new ones.
    /// </summary>
    [Fact]
    public async Task Dispose_PendingCalls_FailsThemAndRefusesNew()
    {
        // Arrange
        A2UIRendererFunctionCalls calls = new();
        A2UIRendererFunctionCall pending = calls.Call(Echo("hi"), functionCallId: "c1");

        // Act
        calls.Dispose();
        Action callAgain = () => calls.Call(Echo("hi"));

        // Assert
        (await Should.ThrowAsync<A2UIException>(pending.Result)).Code.ShouldBe(A2UIErrorCodes.Cancelled);
        callAgain.ShouldThrow<A2UIException>().Code.ShouldBe(A2UIErrorCodes.Disposed);
        calls.Resolve(new A2UIFunctionResponse("c1", 1, null)).ShouldBeFalse();
    }

    /// <summary>
    /// Proves that a pending id cannot be reused and that the pending limit holds.
    /// </summary>
    [Fact]
    public void Call_DuplicateIdOrTooMany_Throws()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { MaxPending = 2 });
        calls.Call(Echo("a"), functionCallId: "c1");

        // Act
        Action duplicate = () => calls.Call(Echo("b"), functionCallId: "c1");
        calls.Call(Echo("c"), functionCallId: "c2");
        Action third = () => calls.Call(Echo("d"), functionCallId: "c3");

        // Assert
        duplicate.ShouldThrow<A2UIException>().Code.ShouldBe(A2UIErrorCodes.Duplicate);
        third.ShouldThrow<A2UIException>().Code.ShouldBe(A2UIErrorCodes.TooManyPending);
        calls.PendingCount.ShouldBe(2);
    }

    /// <summary>
    /// Proves that a call the renderer could not read is refused before anything is registered.
    /// </summary>
    [Fact]
    public void Call_MalformedCall_ThrowsArgumentException()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new();
        A2UICall noCatalog = new("echo");
        A2UICall systemName = new("@index") { CatalogId = ToolsId };
        A2UICall badName = new("not-an-id") { CatalogId = ToolsId };
        A2UICall badArg = new("echo") { CatalogId = ToolsId };
        badArg.Args["two words"] = 1;
        A2UICall notFinite = new("echo") { CatalogId = ToolsId };
        notFinite.Args["value"] = A2UIValue.Literal(new JsonArray(JsonValue.Create(double.NaN)));

        // Act
        Action[] refused =
        [
            () => calls.Call(noCatalog),
            () => calls.Call(systemName),
            () => calls.Call(badName),
            () => calls.Call(badArg),
            () => calls.Call(notFinite),
            () => calls.Call(Echo("x"), functionCallId: string.Empty),
            () => calls.Call(Echo("x"), surfaceId: string.Empty),
            () => calls.Call(Echo("x"), timeout: TimeSpan.Zero),
        ];

        // Assert
        foreach (Action call in refused)
        {
            call.ShouldThrow<ArgumentException>();
        }

        calls.PendingCount.ShouldBe(0);
    }

    /// <summary>
    /// Proves that, given the agent's catalogs, a call is refused when the renderer would refuse it: an unknown
    /// catalog or function, an agent function, a <c>rendererOnly</c> function, one that needs user activation, or
    /// literal arguments that fail the schema.
    /// </summary>
    /// <param name="catalogId">The call's catalog.</param>
    /// <param name="name">The function.</param>
    /// <param name="code">The expected error code.</param>
    [Theory]
    [InlineData("urn:other", "echo", A2UIErrorCodes.UnknownFunction)]
    [InlineData(ToolsId, "missing", A2UIErrorCodes.UnknownFunction)]
    [InlineData(ToolsId, "lookup", A2UIErrorCodes.UnknownFunction)]
    [InlineData(ToolsId, "secret", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData(ToolsId, "gesture", A2UIErrorCodes.InvalidFunctionCall)]
    [InlineData(ToolsId, "echo", A2UIErrorCodes.InvalidFunctionCall)]
    public void Call_WithCatalogsAgentMayNotCall_Throws(string catalogId, string name, string code)
    {
        // Arrange
        Catalog catalog = RendererCatalog();
        A2UIAgentFunctions agentFunctions = new A2UIAgentFunctions([catalog]).Add(ToolsId, "lookup", (_, _) => ValueTask.FromResult<JsonNode?>(null));
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [catalog], AgentFunctions = agentFunctions });
        A2UICall call = new(name) { CatalogId = catalogId };

        // Act
        Action make = () => calls.Call(call);

        // Assert
        make.ShouldThrow<A2UIException>().Code.ShouldBe(code);
        calls.PendingCount.ShouldBe(0);
    }

    /// <summary>
    /// Proves that, given the agent's catalogs, a call nested in the arguments is refused when the renderer would
    /// refuse it, and that a nested call the agent may make is accepted.
    /// </summary>
    /// <param name="nested">The nested <c>@call</c> name.</param>
    /// <param name="accepted">Whether the call is accepted.</param>
    [Theory]
    [InlineData("echo", true)]
    [InlineData("secret", false)]
    [InlineData("gesture", false)]
    [InlineData("lookup", false)]
    [InlineData("missing", false)]
    [InlineData("@index", false)]
    public void Call_WithCatalogsNestedCall_ChecksIt(string nested, bool accepted)
    {
        // Arrange
        Catalog catalog = RendererCatalog();
        A2UIAgentFunctions agentFunctions = new A2UIAgentFunctions([catalog]).Add(ToolsId, "lookup", (_, _) => ValueTask.FromResult<JsonNode?>(null));
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [catalog], AgentFunctions = agentFunctions });
        A2UICall inner = new(nested);
        inner.Args["value"] = "x";
        A2UICall call = Echo(inner);

        // Act
        Action make = () => calls.Call(call);

        // Assert
        if (accepted)
        {
            make.ShouldNotThrow();
        }
        else
        {
            make.ShouldThrow<A2UIException>().Code.ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
        }
    }

    /// <summary>
    /// Proves that a catalog for another protocol version is not used, as the renderer does not use it.
    /// </summary>
    [Fact]
    public void Call_CatalogForOtherVersion_Throws()
    {
        // Arrange
        Catalog current = RendererCatalog();
        Catalog old = new("urn:old", [], current.Functions.Values, "0.9");
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [old] });
        A2UICall call = new("echo") { CatalogId = "urn:old" };
        call.Args["value"] = "x";

        // Act
        Action make = () => calls.Call(call);

        // Assert
        make.ShouldThrow<A2UIException>().Code.ShouldBe(A2UIErrorCodes.UnknownFunction);
    }

    /// <summary>
    /// Proves that, given the agent's catalogs, a call whose arguments are bindings is accepted, since they are only
    /// known once the renderer resolves them.
    /// </summary>
    [Fact]
    public void Call_WithCatalogsAndBoundArgs_IsAccepted()
    {
        // Arrange
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [RendererCatalog()] });

        // Act
        A2UIRendererFunctionCall pending = calls.Call(Echo(A2UIValue.Path("/name")), "main");

        // Assert
        calls.IsPending(pending.FunctionCallId).ShouldBeTrue();
        pending.FunctionCallId.ShouldStartWith("call-");
    }

    /// <summary>
    /// Proves the whole round trip: a renderer runs the call against its surface's data model, and its response
    /// settles the agent's call.
    /// </summary>
    [Fact]
    public async Task Call_RendererRunsFunction_ResultHasValue()
    {
        // Arrange
        List<JsonObject> sent = [];
        Catalog catalog = RendererCatalog();
        using MessageProcessor renderer = new([catalog])
        {
            Outbound = message =>
            {
                sent.Add(message.Message);
                return ValueTask.CompletedTask;
            },
        };
        renderer.ProcessOrThrow(A2UIMessages.CreateSurface("main", dataModel: new JsonObject { ["name"] = "Ada" }, catalogId: ToolsId));
        using A2UIRendererFunctionCalls calls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [catalog] });
        A2UIRendererFunctionCall pending = calls.Call(Echo(A2UIValue.Path("/name")), "main");

        // Act
        renderer.ProcessOrThrow(pending.Message);
        await renderer.WhenIdleAsync();
        bool taken = calls.Resolve(sent.ShouldHaveSingleItem());

        // Assert
        taken.ShouldBeTrue();
        (await pending.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.GetValue<string>().ShouldBe("Ada");
    }

    /// <summary>
    /// Builds a call to the test catalog's <c>echo</c> function.
    /// </summary>
    /// <param name="value">The value to echo.</param>
    private static A2UICall Echo(A2UIValue value)
    {
        A2UICall call = new("echo") { CatalogId = ToolsId };
        call.Args["value"] = value;
        return call;
    }

    /// <summary>
    /// Builds the renderer's catalog: <c>echo</c>, which the agent may call; <c>gesture</c>, which needs user
    /// activation; <c>secret</c>, which is renderer only; and <c>lookup</c>, an agent function.
    /// </summary>
    private static Catalog RendererCatalog()
    {
        static ValueTask<JsonNode?> Run(FunctionInvocation invocation) => ValueTask.FromResult(invocation.Arg("value")?.DeepClone());
        JsonObject schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""")!.AsObject();
        FunctionDefinition[] functions =
        [
            new FunctionDefinition("echo") { AllowedCallers = AllowedCallers.RendererOrAgent, ArgsSchema = schema }.WithImplementation(Run),
            new FunctionDefinition("gesture") { AllowedCallers = AllowedCallers.RendererOrAgent, RequiresUserActivation = true }.WithImplementation(Run),
            new FunctionDefinition("secret").WithImplementation(Run),
            new FunctionDefinition("lookup") { AllowedCallers = AllowedCallers.RendererOrAgent },
        ];
        return new Catalog(ToolsId, [], functions, A2UIProtocol.Version);
    }
}
