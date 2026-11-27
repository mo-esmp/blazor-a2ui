using System.Net;
using BlazorA2UI.Client;

namespace BlazorA2UI.Tests.Client;

/// <summary>
/// Tests for <see cref="A2UIAgentClient"/> against an agent in memory.
/// </summary>
public sealed class A2UIAgentClientTests
{
    /// <summary>
    /// A message that creates a surface with one text component.
    /// </summary>
    private const string CreateSurface = """{"version":"1.0","createSurface":{"surfaceId":"main","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","components":[{"id":"root","component":"Text","text":"Hello"}]}}""";

    /// <summary>
    /// An action message from the renderer.
    /// </summary>
    private const string Action = """{"version":"1.0","action":{"name":"submit","surfaceId":"main","sourceComponentId":"root","timestamp":"2026-11-27T10:00:00.000Z","context":{}}}""";

    /// <summary>
    /// Proves that a text turn posts the text with the renderer's capabilities, streams JSON Lines into the processor,
    /// keeps the conversation id and ends.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_JsonLines_ProcessesMessagesAndEnds()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.JsonLines(CreateSurface + "\n")));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);
        List<A2UIConnectionState> states = [];
        client.StateChanged += () => states.Add(client.State);

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        processor.Surfaces.ContainsKey("main").ShouldBeTrue();
        states.ShouldBe([A2UIConnectionState.Connecting, A2UIConnectionState.Streaming, A2UIConnectionState.Ended]);
        client.ConversationId.ShouldBe("conv1");
        (HttpRequestMessage request, JsonObject body) = agent.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(FakeAgent.Endpoint);
        request.Headers.Accept.ToString().ShouldBe("application/jsonl");
        body["text"]!.GetValue<string>().ShouldBe("Hi");

        // The client always sends metadata with the capabilities.
        body["metadata"]!["a2uiRendererCapabilities"].ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that a later turn sends the conversation id the agent issued.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_SecondTurn_SendsConversationId()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.JsonLines(string.Empty)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);
        await client.SendTextAsync("one");

        // Act
        await client.SendTextAsync("two");

        // Assert
        agent.Requests[0].Request.Headers.Contains(A2UIAgentClient.ConversationHeader).ShouldBeFalse();
        agent.Requests[1].Request.Headers.GetValues(A2UIAgentClient.ConversationHeader).ShouldBe(["conv1"]);
    }

    /// <summary>
    /// Proves that server-sent events carry messages into the processor and text to the text event.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_EventStream_ProcessesMessagesAndRaisesText()
    {
        // Arrange
        StreamingBody stream = new();
        stream.Send(": comment\n\nevent: text\ndata: {\"text\":\"Here you go\"}\n\n");
        stream.Send($"event: a2ui\ndata: {CreateSurface}\n\nevent: other\ndata: {{}}\n\n");
        stream.End();
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Streaming(stream, "text/event-stream")));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor, options => options.Format = A2UIAgentStreamFormat.EventStream);
        List<string> texts = [];
        client.TextReceived += texts.Add;

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Ended);
        texts.ShouldBe(["Here you go"]);
        processor.Surfaces.ContainsKey("main").ShouldBeTrue();
        agent.Requests[0].Request.Headers.Accept.ToString().ShouldBe("text/event-stream");
    }

    /// <summary>
    /// Proves that an <c>error</c> event fails the turn with the agent's message.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_ErrorEvent_Fails()
    {
        // Arrange
        StreamingBody stream = new();
        stream.Send("event: error\ndata: {\"message\":\"The agent could not finish its answer.\"}\n\n");
        stream.End();
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Streaming(stream, "text/event-stream")));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor, options => options.Format = A2UIAgentStreamFormat.EventStream);

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe("The agent reported an error: The agent could not finish its answer.");
    }

    /// <summary>
    /// Proves that a broken stream fails the turn with a clear error instead of hanging: a line over the limit, a body
    /// that ends mid-line, a dropped connection, and a body that is not UTF-8.
    /// </summary>
    /// <param name="body">What the agent sends.</param>
    /// <param name="breaks">Whether the connection drops after the body.</param>
    /// <param name="error">The start of the expected error.</param>
    [Theory]
    [InlineData("{\"version\":\"1.0\",\"deleteSurface\":{\"surfaceId\":\"a-surface-id-that-is-too-long\"}}\n", false, "The agent sent a line longer than 64 bytes.")]
    [InlineData("{\"version\":\"1.0\"", false, "The agent's stream ended in the middle of a message.")]
    [InlineData("", true, "The connection to the agent failed.")]
    public async Task SendTextAsync_BrokenStream_Fails(string body, bool breaks, string error)
    {
        // Arrange
        StreamingBody stream = new();
        stream.Send(body);
        if (breaks)
        {
            stream.Break();
        }
        else
        {
            stream.End();
        }

        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Streaming(stream)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor, options => options.MaxLineLength = 64);

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe(error);
    }

    /// <summary>
    /// Proves that a stream that stops sending fails after the read timeout.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_StalledStream_FailsAfterReadTimeout()
    {
        // Arrange
        StreamingBody stream = new();
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Streaming(stream)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor, options => options.ReadTimeout = TimeSpan.FromMilliseconds(100));

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe("The agent sent nothing for 0.1 seconds.");
    }

    /// <summary>
    /// Proves that a refused request fails with the problem's detail, and that a 404 for the conversation forgets its
    /// id so the next turn starts a new one.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_ConversationExpired_FailsAndForgetsId()
    {
        // Arrange
        int calls = 0;
        FakeAgent agent = new((_, _) => Task.FromResult(++calls == 2
            ? FakeAgent.Problem(HttpStatusCode.NotFound, "The conversation is unknown or has expired.")
            : FakeAgent.JsonLines(string.Empty)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);
        await client.SendTextAsync("one");

        // Act
        await client.SendTextAsync("two");
        string? idAfterFailure = client.ConversationId;
        await client.SendTextAsync("three");

        // Assert
        idAfterFailure.ShouldBeNull();
        agent.Requests[2].Request.Headers.Contains(A2UIAgentClient.ConversationHeader).ShouldBeFalse();
        client.State.ShouldBe(A2UIConnectionState.Ended);
        agent.Requests.Count.ShouldBe(3);
    }

    /// <summary>
    /// Proves that a refused request reports the status and the problem's detail.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_Refused_ReportsDetail()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Problem(HttpStatusCode.Conflict, "The agent is still answering.")));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe("The agent refused the request (409): The agent is still answering.");
    }

    /// <summary>
    /// Proves that an answer in another format fails instead of being parsed as A2UI.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_UnknownContentType_Fails()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", System.Text.Encoding.UTF8, "text/html") }));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe("The agent answered with 'text/html', which is neither JSON Lines nor server-sent events.");
    }

    /// <summary>
    /// Proves that actions sent while a turn runs wait for it and then go together in one request.
    /// </summary>
    [Fact]
    public async Task Outbound_ActionsDuringTurn_WaitAndGoTogether()
    {
        // Arrange
        StreamingBody first = new();
        int calls = 0;
        FakeAgent agent = new((_, _) => Task.FromResult(++calls == 1 ? FakeAgent.Streaming(first) : FakeAgent.JsonLines(string.Empty)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);
        TaskCompletionSource streaming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += () =>
        {
            if (client.State == A2UIConnectionState.Streaming)
            {
                streaming.TrySetResult();
            }
        };
        Task turn = client.SendTextAsync("Hi");
        await streaming.Task;

        // Act
        // The client sets the processor's outbound.
        await processor.Outbound!(new A2UIOutboundMessage(JsonNode.Parse(Action)!.AsObject(), null));
        await processor.Outbound!(new A2UIOutboundMessage(JsonNode.Parse(Action)!.AsObject(), null));
        first.End();
        await turn;
        await WaitForAsync(() => agent.Requests.Count == 2 && client.State == A2UIConnectionState.Ended);

        // Assert
        JsonObject body = agent.Requests[1].Body;
        body.ContainsKey("text").ShouldBeFalse();

        // The second request carries the two actions.
        body["messages"]!.AsArray().Count.ShouldBe(2);
    }

    /// <summary>
    /// Proves that an agent function call goes out at once, even while a turn streams, and that a call the agent
    /// cannot take fails at once instead of waiting for its timeout.
    /// </summary>
    [Fact]
    public async Task Outbound_AgentFunctionCallRefused_FailsTheCallAtOnce()
    {
        // Arrange
        StreamingBody turnBody = new();
        FakeAgent agent = new((_, body) => Task.FromResult(body.ContainsKey("text")
            ? FakeAgent.Streaming(turnBody)
            : FakeAgent.Problem(HttpStatusCode.ServiceUnavailable, "Busy.")));
        Catalog tools = new("urn:tools", [], [new FunctionDefinition("lookup") { AllowedCallers = AllowedCallers.RendererOrAgent }], A2UIProtocol.Version);
        using MessageProcessor processor = new([BasicCatalog.Create(), tools]);
        await using A2UIAgentClient client = Client(agent, processor);
        processor.ProcessLine(CreateSurface);
        Task turn = client.SendTextAsync("Hi");

        // Act
        A2UIException error = await Should.ThrowAsync<A2UIException>(
            processor.CallAgentFunctionAsync("main", "urn:tools", "lookup", options: new BlazorA2UI.Rpc.A2UIAgentCallOptions { Timeout = TimeSpan.FromMinutes(5) }));
        turnBody.End();
        await turn;

        // Assert
        error.Code.ShouldBe(A2UIErrorCodes.ExecutionError);
        error.Message.ShouldContain("The agent refused the request (503): Busy.");
    }

    /// <summary>
    /// Proves that cancelling stops the running turn and drops the waiting ones.
    /// </summary>
    [Fact]
    public async Task CancelTurns_WhileStreaming_CancelsAndDropsQueue()
    {
        // Arrange
        StreamingBody stream = new();
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.Streaming(stream)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        await using A2UIAgentClient client = Client(agent, processor);
        TaskCompletionSource streaming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += () =>
        {
            if (client.State == A2UIConnectionState.Streaming)
            {
                streaming.TrySetResult();
            }
        };
        Task first = client.SendTextAsync("one");
        Task second = client.SendTextAsync("two");
        await streaming.Task;

        // Act
        client.CancelTurns();
        await Task.WhenAll(first, second);

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Cancelled);
        agent.Requests.Count.ShouldBe(1);
    }

    /// <summary>
    /// Proves that disposing disconnects the client from the processor and refuses new turns.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ClearsOutboundAndRefusesTurns()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.JsonLines(string.Empty)));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        A2UIAgentClient client = Client(agent, processor);

        // Act
        await client.DisposeAsync();

        // Assert
        processor.Outbound.ShouldBeNull();
        Should.Throw<ObjectDisposedException>(() => client.SendTextAsync("Hi"));
    }

    /// <summary>
    /// Proves that the client works and raises its events on the synchronization context it was created on.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_WithContext_RaisesEventsOnContext()
    {
        // Arrange
        FakeAgent agent = new((_, _) => Task.FromResult(FakeAgent.JsonLines(CreateSurface + "\n")));
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        TestContext context = new();
        A2UIAgentClient client;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            client = Client(agent, processor);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        List<bool> onContext = [];
        client.StateChanged += () => onContext.Add(SynchronizationContext.Current == context);

        // Act
        await client.SendTextAsync("Hi");
        await client.DisposeAsync();

        // Assert
        onContext.ShouldBe([true, true, true]);
    }

    /// <summary>
    /// Proves that options without an absolute http or https endpoint, or with a limit out of range, are refused.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="maxLineLength">The line limit.</param>
    [Theory]
    [InlineData(null, 1024)]
    [InlineData("/a2ui", 1024)]
    [InlineData("ftp://agent.test/a2ui", 1024)]
    [InlineData("https://agent.test/a2ui", 0)]
    public void Constructor_BadOptions_Throws(string? endpoint, int maxLineLength)
    {
        // Arrange
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        A2UIAgentClientOptions options = new()
        {
            Endpoint = endpoint is null ? null : new Uri(endpoint, UriKind.RelativeOrAbsolute),
            MaxLineLength = maxLineLength,
        };

        // Act
        Action create = () => _ = new A2UIAgentClient(new HttpClient(), processor, options);

        // Assert
        create.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that only short base64url conversation ids from the agent are kept.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="expected">Whether it is kept.</param>
    [Theory]
    [InlineData("abc-_DEF0123456789xyzA", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a\r\nb", false)]
    public void IsAcceptableConversationId_Shapes_AreChecked(string id, bool expected)
    {
        // Arrange
        string candidate = id;

        // Act
        bool result = A2UIAgentClient.IsAcceptableConversationId(candidate);

        // Assert
        result.ShouldBe(expected);
    }

    /// <summary>
    /// Proves that the registered client uses the scope's processor and the app's <see cref="HttpClient"/>, and that it
    /// cannot be resolved without one.
    /// </summary>
    [Fact]
    public async Task AddA2UIAgentClient_Resolves_ConnectsScopeProcessor()
    {
        // Arrange
        ServiceCollection withHttp = new();
        withHttp.AddA2UI();
        withHttp.AddA2UIAgentClient(options => options.Endpoint = FakeAgent.Endpoint);
        withHttp.AddScoped(_ => new HttpClient());
        ServiceCollection withoutHttp = new();
        withoutHttp.AddA2UI();
        withoutHttp.AddA2UIAgentClient(options => options.Endpoint = FakeAgent.Endpoint);
        await using ServiceProvider provider = withHttp.BuildServiceProvider();
        await using ServiceProvider missing = withoutHttp.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        // Act
        A2UIAgentClient client = scope.ServiceProvider.GetRequiredService<A2UIAgentClient>();
        Action resolveWithoutHttp = () => missing.GetRequiredService<A2UIAgentClient>();

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Idle);
        scope.ServiceProvider.GetRequiredService<MessageProcessor>().Outbound.ShouldNotBeNull();
        resolveWithoutHttp.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Creates a client for an agent.
    /// </summary>
    /// <returns>The client.</returns>
    /// <param name="agent">The agent.</param>
    /// <param name="processor">The processor.</param>
    /// <param name="configure">Changes the options, or null.</param>
    private static A2UIAgentClient Client(FakeAgent agent, MessageProcessor processor, Action<A2UIAgentClientOptions>? configure = null)
    {
        A2UIAgentClientOptions options = new() { Endpoint = FakeAgent.Endpoint };
        configure?.Invoke(options);
        return new A2UIAgentClient(new HttpClient(agent), processor, options);
    }

    /// <summary>
    /// Waits until a condition holds, for at most five seconds.
    /// </summary>
    /// <param name="condition">The condition.</param>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A synchronization context that runs posted work on the thread pool with itself set as current.
    /// </summary>
    private sealed class TestContext : SynchronizationContext
    {
        /// <inheritdoc />
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SynchronizationContext? previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
    }
}
