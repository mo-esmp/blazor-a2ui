namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIRendererMessageProcessor"/>: the envelope, and the <c>action</c>, <c>error</c> and
/// <c>rendererFunctionResponse</c> callbacks.
/// </summary>
public sealed class A2UIRendererMessageProcessorTests
{
    /// <summary>
    /// A valid <c>action</c> message.
    /// </summary>
    private const string ActionMessage =
        """{"version":"1.0","action":{"name":"submit","surfaceId":"main","sourceComponentId":"send","timestamp":"2026-11-26T10:15:00.250Z","context":{"email":"a@b.c"},"userMessage":"Sent","metadata":{"extensions":{"x":1}}}}""";

    /// <summary>
    /// Proves that an action reaches its callback as a typed copy and is handled without replies.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_Action_PassesTypedActionToCallback()
    {
        // Arrange
        A2UIRendererAction? received = null;
        A2UIRendererMessageProcessor processor = new()
        {
            OnAction = (action, _) =>
            {
                received = action;
                return ValueTask.FromResult(true);
            },
        };

        // Act
        A2UIRendererMessageResult result = await processor.ProcessLineAsync(ActionMessage);

        // Assert
        result.Handled.ShouldBeTrue();
        result.Replies.ShouldBeEmpty();
        received.ShouldNotBeNull();
        (received.Name, received.SurfaceId, received.SourceComponentId, received.UserMessage).ShouldBe(("submit", "main", "send", "Sent"));
        received.Timestamp.ShouldBe(new DateTimeOffset(2026, 11, 26, 10, 15, 0, 250, TimeSpan.Zero));
        received.Context.ToJsonString().ShouldBe("""{"email":"a@b.c"}""");
        received.Metadata!.ToJsonString().ShouldBe("""{"extensions":{"x":1}}""");
    }

    /// <summary>
    /// Proves that a message without a callback, or one the callback declines, is not handled, so it can go to the
    /// model's turn, and that a message without a callback is not checked.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoCallbackOrDeclined_NotHandled()
    {
        // Arrange
        A2UIRendererMessageProcessor without = new();
        A2UIRendererMessageProcessor declining = new()
        {
            OnAction = (_, _) => ValueTask.FromResult(false),
            OnError = (_, _) => ValueTask.FromResult(false),
            OnRendererFunctionResponse = (_, _) => ValueTask.FromResult(false),
        };
        JsonObject error = JsonNode.Parse("""{"version":"1.0","error":{"code":"EXECUTION_ERROR","message":"m","surfaceId":"main"}}""")!.AsObject();
        JsonObject response = JsonNode.Parse("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c1","value":1}}""")!.AsObject();
        JsonObject malformedAction = JsonNode.Parse("""{"version":"1.0","action":{"name":"submit"}}""")!.AsObject();

        // Act
        A2UIRendererMessageResult[] results =
        [
            await without.ProcessLineAsync(ActionMessage),
            await without.ProcessAsync(error),
            await without.ProcessAsync(response),
            await without.ProcessAsync(malformedAction),
            await declining.ProcessLineAsync(ActionMessage),
            await declining.ProcessAsync(error),
            await declining.ProcessAsync(response),
        ];

        // Assert
        results.ShouldAllBe(r => !r.Handled && r.Replies.Count == 0);
    }

    /// <summary>
    /// Proves that anything other than one v1.0 renderer-to-agent message is rejected.
    /// </summary>
    /// <param name="json">The message.</param>
    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"action":{}}""")]
    [InlineData("""{"version":"0.9","action":{}}""")]
    [InlineData("""{"version":"1.0"}""")]
    [InlineData("""{"version":"1.0","action":{},"error":{}}""")]
    [InlineData("""{"version":"1.0","createSurface":{}}""")]
    [InlineData("""{"version":"1.0","error":"oops"}""")]
    [InlineData("""{"version":"1.0","version":"1.0","error":{"code":"X","message":"m","surfaceId":"s"}}""")]
    [InlineData("""{"version":"1.0","error":{"code":"X","code":"Y","message":"m","surfaceId":"s"}}""")]
    public async Task ProcessAsync_NotOneRendererMessage_Throws(string json)
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new();

        // Act
        Func<Task> process = () => processor.ProcessLineAsync(json);

        // Assert
        await process.ShouldThrowAsync<A2UIValidationException>();
    }

    /// <summary>
    /// Proves that a line that is not JSON is rejected.
    /// </summary>
    [Fact]
    public async Task ProcessLineAsync_NotJson_Throws()
    {
        // Arrange
        A2UIRendererMessageProcessor processor = new();

        // Act
        Func<Task> process = () => processor.ProcessLineAsync("{not json");

        // Assert
        await process.ShouldThrowAsync<A2UIValidationException>();
    }

    /// <summary>
    /// Proves that an action that fails the schema is rejected before its callback runs.
    /// </summary>
    /// <param name="body">The <c>action</c> body.</param>
    [Theory]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00Z"}""")]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26 10:15","context":{}}""")]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00","context":{}}""")]
    [InlineData("""{"name":1,"surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00Z","context":{}}""")]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00Z","context":{},"userMessage":2}""")]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00Z","context":{},"metadata":{"other":{}}}""")]
    [InlineData("""{"name":"a","surfaceId":"s","sourceComponentId":"c","timestamp":"2026-11-26T10:15:00Z","context":{},"metadata":{"extensions":{"com-example":1}}}""")]
    public async Task ProcessAsync_MalformedAction_Throws(string body)
    {
        // Arrange
        int calls = 0;
        A2UIRendererMessageProcessor processor = new()
        {
            OnAction = (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(true);
            },
        };

        // Act
        Func<Task> process = () => processor.ProcessLineAsync($$"""{"version":"1.0","action":{{body}}}""");

        // Assert
        await process.ShouldThrowAsync<A2UIValidationException>();
        calls.ShouldBe(0);
    }

    /// <summary>
    /// Proves that the two forms of <c>error</c> are read and checked: a validation error needs a surface and a path,
    /// and any other error belongs to exactly one of a surface and a function call.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_Errors_FollowSpecForms()
    {
        // Arrange
        List<A2UIRendererError> received = [];
        A2UIRendererMessageProcessor processor = new()
        {
            OnError = (error, _) =>
            {
                received.Add(error);
                return ValueTask.FromResult(true);
            },
        };

        // Act
        await processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"VALIDATION_FAILED","message":"Bad","surfaceId":"main","path":"/components/0"}}""");
        await processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"EXECUTION_ERROR","message":"Failed","functionCallId":"c1","detail":"x"}}""");
        Func<Task> validationWithoutPath = () => processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"VALIDATION_FAILED","message":"m","surfaceId":"main"}}""");
        Func<Task> validationWithExtra = () => processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"UNALLOWED_CHILD","message":"m","surfaceId":"main","path":"/","x":1}}""");
        Func<Task> genericWithBoth = () => processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"X","message":"m","surfaceId":"main","functionCallId":"c1"}}""");
        Func<Task> genericWithNeither = () => processor.ProcessLineAsync("""{"version":"1.0","error":{"code":"X","message":"m"}}""");

        // Assert
        received.ShouldBe(
        [
            new A2UIRendererError(A2UIErrorCodes.ValidationFailed, "Bad", "main", null, "/components/0"),
            new A2UIRendererError(A2UIErrorCodes.ExecutionError, "Failed", null, "c1", null),
        ]);
        await validationWithoutPath.ShouldThrowAsync<A2UIValidationException>();
        await validationWithExtra.ShouldThrowAsync<A2UIValidationException>();
        await genericWithBoth.ShouldThrowAsync<A2UIValidationException>();
        await genericWithNeither.ShouldThrowAsync<A2UIValidationException>();
    }

    /// <summary>
    /// Proves that a renderer function response reaches its callback as a value or an error, and that one with both,
    /// neither or extra keys is rejected.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_RendererFunctionResponse_ReadsValueOrError()
    {
        // Arrange
        List<A2UIFunctionResponse> received = [];
        A2UIRendererMessageProcessor processor = new()
        {
            OnRendererFunctionResponse = (response, _) =>
            {
                received.Add(response);
                return ValueTask.FromResult(true);
            },
        };

        // Act
        await processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c1","value":null}}""");
        await processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c2","error":{"code":"EXECUTION_ERROR","message":"m"}}}""");
        Func<Task> both = () => processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c3","value":1,"error":{"code":"X","message":"m"}}}""");
        Func<Task> neither = () => processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c3"}}""");
        Func<Task> extra = () => processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c3","value":1,"x":2}}""");
        Func<Task> errorExtra = () => processor.ProcessLineAsync("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c3","error":{"code":"X","message":"m","x":1}}}""");

        // Assert
        received.Count.ShouldBe(2);
        received[0].IsError.ShouldBeFalse();
        received[0].Value.ShouldBeNull();
        received[1].Error.ShouldBe(new A2UIFunctionError(A2UIErrorCodes.ExecutionError, "m"));
        await both.ShouldThrowAsync<A2UIValidationException>();
        await neither.ShouldThrowAsync<A2UIValidationException>();
        await extra.ShouldThrowAsync<A2UIValidationException>();
        await errorExtra.ShouldThrowAsync<A2UIValidationException>();
    }
}
