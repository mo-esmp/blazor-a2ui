using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
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
    /// Wraps a <c>createSurface</c> body in a v1.0 envelope.
    /// </summary>
    /// <param name="createSurface">The <c>createSurface</c> body, as JSON.</param>
    private static JsonNode? Message(string createSurface) =>
        JsonNode.Parse($$$"""{"version":"1.0","createSurface":{{{createSurface}}} }""");
}
