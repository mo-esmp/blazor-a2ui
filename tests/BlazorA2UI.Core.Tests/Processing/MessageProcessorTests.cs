using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using BlazorA2UI.Rpc;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Processing;

/// <summary>
/// Tests for <see cref="MessageProcessor"/> behavior that the conformance suites do not cover.
/// </summary>
public sealed class MessageProcessorTests
{
    /// <summary>
    /// The id of the basic catalog the tests create surfaces with.
    /// </summary>
    private static readonly string s_basicId = BasicCatalog.CatalogId;

    /// <summary>
    /// Proves that a <c>createSurface</c> rejected while applying its components leaves no surface behind, so the id can be used again.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_RejectedCreateSurface_PublishesNoSurface()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        JsonNode? bad = Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}","components":[]}""");

        // Act
        Action process = () => processor.ProcessOrThrow(bad, A2UIValidationConfig.Strict);

        // Assert
        process.ShouldThrow<A2UIException>();
        processor.Surfaces.ShouldNotContainKey("s");
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        processor.Surfaces.ShouldContainKey("s");
    }

    /// <summary>
    /// Proves that a function call on a surface cannot use a catalog registered after the surface was created.
    /// </summary>
    [Fact]
    public void Evaluate_CatalogAddedAfterCreateSurface_ReportsCatalogError()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        processor.AddCatalog(BasicCatalog.Create().WithCatalogId("test:later"));
        Surface surface = processor.Surfaces["s"];
        JsonNode? call = JsonNode.Parse("""{"@call":"not","catalogId":"test:later","args":{"value":true}}""");

        // Act
        surface.Evaluate(call, surface.RootContext, out _, out IReadOnlyList<A2UIException> errors);

        // Assert
        errors.ShouldContain(e => e.Code == A2UIErrorCodes.CatalogError);
    }

    /// <summary>
    /// Proves that strict validation reports <c>@accessibility</c> values that break the accessibility schema.
    /// </summary>
    /// <param name="accessibility">The <c>@accessibility</c> object, as JSON.</param>
    [Theory]
    [InlineData("""{"live":"loud"}""")]
    [InlineData("""{"hidden":"yes"}""")]
    [InlineData("""{"label":42}""")]
    public void ProcessOrThrow_InvalidAccessibilityUnderStrictValidation_ReportsError(string accessibility)
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        JsonNode? message = Message($$$"""
            {"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[{"id":"root","component":"Text","text":"hi","@accessibility":{{{accessibility}}} } ] }
            """);

        // Act
        processor.ProcessOrThrow(message, A2UIValidationConfig.Strict);

        // Assert
        errors.ShouldContain(e => e.Message.Contains("@accessibility", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves that valid <c>@accessibility</c> values, including a bound <c>hidden</c>, pass strict validation.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_ValidAccessibilityUnderStrictValidation_ReportsNothing()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        JsonNode? message = Message($$$"""
            {"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[{"id":"root","component":"Text","text":"hi",
               "@accessibility":{"label":"Greeting","live":"polite","hidden":{"@path":"/hide"} } } ] }
            """);

        // Act
        processor.ProcessOrThrow(message, A2UIValidationConfig.Strict);

        // Assert
        errors.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a <c>path</c> key inside data-model values is application data and is not checked as a JSON Pointer.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_PathKeyInDataModelValue_StoresValue()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        JsonNode? update = JsonNode.Parse("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/","value":{"path":"~/docs"}}}""");

        // Act
        processor.ProcessOrThrow(update);

        // Assert
        processor.Surfaces["s"].DataModel.Get("/path").ShouldNotBeNull().GetValue<string>().ShouldBe("~/docs");
    }

    /// <summary>
    /// Proves that the update's own <c>path</c> is still checked as a JSON Pointer.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_InvalidUpdatePath_ThrowsValidationError()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        JsonNode? update = JsonNode.Parse("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/a~2b","value":1}}""");

        // Act
        Action process = () => processor.ProcessOrThrow(update);

        // Assert
        process.ShouldThrow<A2UIValidationException>();
    }

    /// <summary>
    /// Proves that the outbound data-model metadata keeps a root that is an array instead of replacing it with an empty object.
    /// </summary>
    [Fact]
    public void GetRendererDataModel_ArrayRoot_ReturnsArray()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}","sendDataModel":true}"""));
        processor.ProcessOrThrow(JsonNode.Parse("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/","value":[1,2]}}"""));

        // Act
        JsonObject? metadata = processor.GetRendererDataModel();

        // Assert
        metadata.ShouldNotBeNull()["surfaces"].ShouldNotBeNull()["s"].ShouldNotBeNull().ToJsonString().ShouldBe("[1,2]");
    }

    /// <summary>
    /// Proves that an <c>agentFunctionResponse</c> with a malformed <c>error</c> is rejected and does not settle the call as a success.
    /// </summary>
    /// <param name="error">The <c>error</c> value, as JSON.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("\"boom\"")]
    [InlineData("""{"code":"X"}""")]
    public async Task Process_MalformedErrorResponse_LeavesCallPendingAsync(string error)
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()], new A2UIProcessorOptions { AgentCallTimeout = Timeout.InfiniteTimeSpan });
        List<A2UIOutboundMessage> sent = [];
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        processor.Outbound = m =>
        {
            lock (sent)
            {
                sent.Add(m);
            }

            return ValueTask.CompletedTask;
        };
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        Surface surface = processor.Surfaces["s"];
        Task<JsonNode?> call = surface.AgentCalls.CallAsync(s_basicId, "lookup", new JsonObject(), CancellationToken.None);
        await processor.WhenIdleAsync();
        string id = sent.Single(m => m.Kind == "callAgentFunction").Body["functionCallId"].ShouldNotBeNull().GetValue<string>();

        // Act
        processor.Process(JsonNode.Parse($$$"""{"version":"1.0","agentFunctionResponse":{"functionCallId":"{{{id}}}","error":{{{error}}} } }"""));

        // Assert
        errors.ShouldContain(e => e.Code == A2UIErrorCodes.ValidationFailed);
        call.IsCompleted.ShouldBeFalse();
        surface.AgentCalls.CancelAll("Test finished.");
        Func<Task> wait = () => call;
        await wait.ShouldThrowAsync<A2UIException>();
    }

    /// <summary>
    /// Proves that a stream line that repeats a property name is reported as a validation error instead of throwing.
    /// </summary>
    [Fact]
    public void ProcessLine_DuplicateProperty_ReportsValidationError()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        string line = $$$"""{"version":"1.0","version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}}""";

        // Act
        Action process = () => processor.ProcessLine(line);

        // Assert
        process.ShouldNotThrow();
        errors.ShouldContain(e => e.Code == A2UIErrorCodes.ValidationFailed);
        processor.Surfaces.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a parsed node with a repeated property name, nested or at the top, is reported instead of throwing out of <see cref="MessageProcessor.Process"/>.
    /// </summary>
    /// <param name="json">The message, as JSON.</param>
    [Theory]
    [InlineData("""{"version":"1.0","version":"1.0","deleteSurface":{"surfaceId":"s"}}""")]
    [InlineData("""{"version":"1.0","deleteSurface":{"surfaceId":"s","surfaceId":"t"}}""")]
    public void Process_DuplicateProperty_ReportsValidationError(string json)
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        JsonNode? message = JsonNode.Parse(json);

        // Act
        Action process = () => processor.Process(message);

        // Assert
        process.ShouldNotThrow();
        errors.ShouldContain(e => e.Code == A2UIErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// Proves that a catalog for another protocol version can be registered but no surface can be created with it, and it is not advertised.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_CreateSurfaceWithOtherVersionCatalog_ThrowsCatalogNotFound()
    {
        // Arrange
        Catalog old = new("test:v09", [], [], protocolVersion: "0.9");
        MessageProcessor processor = new([BasicCatalog.Create(), old]);

        // Act
        Action create = () => processor.ProcessOrThrow(Message("""{"surfaceId":"s","catalogId":"test:v09"}"""));

        // Assert
        create.ShouldThrow<A2UICatalogException>().Message.ShouldContain("Catalog not found: test:v09");
        processor.Surfaces.ShouldBeEmpty();
        processor.GetRendererCapabilities().ToJsonString().ShouldNotContain("test:v09");
    }

    /// <summary>
    /// Proves that cancelling the token of a host agent call fails it with CANCELLED and frees its id for a new call.
    /// </summary>
    [Fact]
    public async Task CallAgentFunctionAsync_TokenCancelled_FailsWithCancelledAndFreesIdAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()], new A2UIProcessorOptions { AgentCallTimeout = Timeout.InfiniteTimeSpan });
        processor.Outbound = _ => ValueTask.CompletedTask;
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        using CancellationTokenSource cancel = new();
        A2UIAgentCallOptions options = new() { FunctionCallId = "fc-1" };
        Task<JsonNode?> call = processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, options, cancel.Token);

        // Act
        await cancel.CancelAsync();
        A2UIException error = await Should.ThrowAsync<A2UIException>(() => call);

        // Assert
        error.Code.ShouldBe(A2UIErrorCodes.Cancelled);
        processor.IsAgentCallPending("fc-1").ShouldBeFalse();
        Task<JsonNode?> again = processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, options);
        again.IsFaulted.ShouldBeFalse();
        processor.Dispose();
        (await Should.ThrowAsync<A2UIException>(() => again)).Code.ShouldBe(A2UIErrorCodes.Cancelled);
    }

    /// <summary>
    /// Proves that disposing a processor twice is safe and that later host agent calls fail with DISPOSED without sending anything.
    /// </summary>
    [Fact]
    public async Task CallAgentFunctionAsync_AfterDispose_FailsWithDisposedAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        int sent = 0;
        processor.Outbound = _ =>
        {
            sent++;
            return ValueTask.CompletedTask;
        };
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        processor.Dispose();

        // Act
        processor.Dispose();
        A2UIException error = await Should.ThrowAsync<A2UIException>(() => processor.CallAgentFunctionAsync("s", s_basicId, "lookup"));

        // Assert
        error.Code.ShouldBe(A2UIErrorCodes.Disposed);
        sent.ShouldBe(0);
        processor.IsDisposed.ShouldBeTrue();
    }

    /// <summary>
    /// Proves that a message processed reentrantly, from a handler running inside a user-activated batch, does not
    /// inherit the outer gesture, while the same call in the activated batch itself runs.
    /// </summary>
    [Fact]
    public async Task Process_ReentrantDuringActivatedBatch_DoesNotInheritActivationAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create(), ToolsCatalog()]);
        List<A2UIOutboundMessage> sent = Record(processor);
        processor.SurfaceCreated += _ => processor.Process(RendererCall("inner", "gesture", "{}"));
        A2UIExecutionContext activated = new() { IsUserActivated = true };

        // Act
        processor.ProcessWithContextOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""), activated);
        processor.ProcessWithContextOrThrow(RendererCall("outer", "gesture", "{}"), activated);
        await processor.WhenIdleAsync();

        // Assert
        JsonObject inner = Response(sent, "inner");
        inner["error"]?["code"]?.GetValue<string>().ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
        inner["error"]?["message"]?.GetValue<string>().ShouldContain("user activation");
        Response(sent, "outer").ContainsKey("error").ShouldBeFalse();
    }

    /// <summary>
    /// Proves that <c>ProcessOrThrow(message, null)</c> still compiles and binds to the validation overload, so hosts
    /// written against the earlier API keep building.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_NullValidation_UsesProcessorDefaults()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);

        // Act
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""), null);

        // Assert
        processor.Surfaces.ShouldContainKey("s");
    }

    /// <summary>
    /// Proves that an RPC argument holding <c>${...}</c> text is data for a function that does not interpret templates:
    /// the renderer-only call named inside it is not checked, and the text comes back unchanged.
    /// </summary>
    [Fact]
    public async Task Process_RpcLiteralTemplateText_IsNotPreflightedAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create(), ToolsCatalog()]);
        List<A2UIOutboundMessage> sent = Record(processor);

        // Act
        processor.Process(RendererCall("lit", "echo", """{"value":"${secret()}"}"""));
        await processor.WhenIdleAsync();

        // Assert
        JsonObject response = Response(sent, "lit");
        response.ContainsKey("error").ShouldBeFalse(response.ToJsonString());
        response["value"]?.GetValue<string>().ShouldBe("${secret()}");
    }

    /// <summary>
    /// Proves that a function that does interpret its argument as a template still refuses a renderer-only call
    /// inside it, before evaluating any part.
    /// </summary>
    [Fact]
    public async Task Process_RpcTemplateCrossingBoundary_IsRefusedAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create(), ToolsCatalog()]);
        List<A2UIOutboundMessage> sent = Record(processor);

        // Act
        processor.Process(RendererCall("tpl", "format", """{"value":"${missing()}${secret()}"}"""));
        await processor.WhenIdleAsync();

        // Assert
        JsonObject response = Response(sent, "tpl");
        response["error"]?["code"]?.GetValue<string>().ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
        response["error"]?["message"]?.GetValue<string>().ShouldContain("secret");
    }

    /// <summary>
    /// The id of the test catalog that <see cref="ToolsCatalog"/> builds.
    /// </summary>
    private const string ToolsId = "https://example.com/tools.json";

    /// <summary>
    /// Builds a v1.0 catalog of agent-callable test functions: <c>echo</c> returns its <c>value</c>, <c>format</c>
    /// interprets it as a template (with the basic <c>formatString</c>), <c>gesture</c> needs user activation and
    /// <c>secret</c> is renderer-only.
    /// </summary>
    private static Catalog ToolsCatalog()
    {
        A2UIFunction format = BasicCatalog.Create().Functions["formatString"].Implementation
            ?? throw new InvalidOperationException("The basic catalog has no formatString implementation.");
        FunctionDefinition[] functions =
        [
            new FunctionDefinition("echo") { AllowedCallers = AllowedCallers.RendererOrAgent }
                .WithImplementation(i => ValueTask.FromResult(i.Arg("value")?.DeepClone())),
            new FunctionDefinition("format") { AllowedCallers = AllowedCallers.RendererOrAgent }.WithImplementation(format),
            new FunctionDefinition("gesture") { AllowedCallers = AllowedCallers.RendererOrAgent, RequiresUserActivation = true }
                .WithImplementation(_ => ValueTask.FromResult<JsonNode?>(JsonValue.Create(true))),
            new FunctionDefinition("secret") { AllowedCallers = AllowedCallers.RendererOnly }
                .WithImplementation(_ => ValueTask.FromResult<JsonNode?>(JsonValue.Create("leaked"))),
        ];
        return new Catalog(ToolsId, [], functions, A2UIProtocol.Version);
    }

    /// <summary>
    /// Builds an agent's <c>callRendererFunction</c> for a function of <see cref="ToolsCatalog"/>.
    /// </summary>
    /// <param name="functionCallId">The call's id.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments, as a JSON object.</param>
    private static JsonNode? RendererCall(string functionCallId, string name, string args) =>
        JsonNode.Parse($$$$$"""{"version":"1.0","callRendererFunction":{"functionCallId":"{{{{{functionCallId}}}}}","callFunction":{"@call":"{{{{{name}}}}}","catalogId":"{{{{{ToolsId}}}}}","args":{{{{{args}}}}}}}}""");

    /// <summary>
    /// Records every message the processor sends.
    /// </summary>
    /// <param name="processor">The processor to listen to.</param>
    private static List<A2UIOutboundMessage> Record(MessageProcessor processor)
    {
        List<A2UIOutboundMessage> sent = [];
        processor.Outbound = m =>
        {
            lock (sent)
            {
                sent.Add(m);
            }

            return ValueTask.CompletedTask;
        };
        return sent;
    }

    /// <summary>
    /// Finds the <c>rendererFunctionResponse</c> sent for a call.
    /// </summary>
    /// <param name="sent">The messages sent.</param>
    /// <param name="functionCallId">The call's id.</param>
    private static JsonObject Response(List<A2UIOutboundMessage> sent, string functionCallId) =>
        sent.Select(m => m.Message["rendererFunctionResponse"] as JsonObject)
            .Where(r => r?["functionCallId"]?.GetValue<string>() == functionCallId)
            .ShouldHaveSingleItem()
            .ShouldNotBeNull();

    /// <summary>
    /// Wraps a <c>createSurface</c> body in a v1.0 envelope.
    /// </summary>
    /// <param name="createSurface">The <c>createSurface</c> body, as JSON.</param>
    private static JsonNode? Message(string createSurface) =>
        JsonNode.Parse($$$"""{"version":"1.0","createSurface":{{{createSurface}}} }""");
}
