using System.Net;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.AspNetCore;

/// <summary>
/// Tests for the endpoint that <see cref="A2UIAgentEndpointExtensions.MapA2UIAgent"/> maps, over real HTTP.
/// </summary>
public sealed class A2UIAgentEndpointTests
{
    /// <summary>
    /// The id of the catalog that declares the test functions.
    /// </summary>
    private const string ToolsId = "urn:tools";

    /// <summary>
    /// Proves that a turn's messages stream back as JSON Lines, with a new conversation id in the header.
    /// </summary>
    [Fact]
    public async Task Post_Text_StreamsMessagesAsJsonLines()
    {
        // Arrange
        string? received = null;
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            received = turn.Text;
            await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hello" }), ct);
            await turn.SendAsync(A2UIMessages.UpdateDataModel("main", "/name", "Ada"), ct);
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/jsonl");
        response.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single().Length.ShouldBe(22);
        received.ShouldBe("Hi");
        lines.Count.ShouldBe(2);
        lines[0].ContainsKey("createSurface").ShouldBeTrue();
        lines[1].ContainsKey("updateDataModel").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that server-sent events carry text as well as messages.
    /// </summary>
    [Fact]
    public async Task Post_EventStream_SendsTextAndMessages()
    {
        // Arrange
        bool canSendText = false;
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            canSendText = turn.CanSendText;
            await turn.SendTextAsync("Here is the form.", ct);
            await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hello" }), ct);
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}""", eventStream: true));
        List<(string Event, JsonObject Data)> events = await TestAgentHost.ReadEventsAsync(response);

        // Assert
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        canSendText.ShouldBeTrue();
        events.Select(e => e.Event).ShouldBe(["text", "a2ui"]);
        events[0].Data["text"]!.GetValue<string>().ShouldBe("Here is the form.");
        events[1].Data.ContainsKey("createSurface").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that JSON Lines carry only messages: text is dropped, and the turn can tell.
    /// </summary>
    [Fact]
    public async Task Post_JsonLines_DropsText()
    {
        // Arrange
        bool canSendText = true;
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            canSendText = turn.CanSendText;
            await turn.SendTextAsync("Not sent.", ct);
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        canSendText.ShouldBeFalse();
        lines.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a model's answer is streamed: text and valid messages are sent, and a message the renderer would
    /// reject is held back and returned as an error.
    /// </summary>
    [Fact]
    public async Task SendModelResponseAsync_MixedAnswer_SendsValidPartsAndReturnsErrors()
    {
        // Arrange
        string create = A2UIMessages.CreateSurface("main", new Text { Content = "Hello" }).ToJsonString();
        string orphan = A2UIMessages.UpdateDataModel("missing", "/x", 1).ToJsonString();
        string[] chunks = ["Here:\n<a2ui-", $"json>[{create},", $"{orphan}]</a2ui-json>", " Done."];
        IReadOnlyList<A2UIErrorPart> errors = [];
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            errors = await turn.SendModelResponseAsync(ToAsync(chunks), cancellationToken: ct);
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}""", eventStream: true));
        List<(string Event, JsonObject Data)> events = await TestAgentHost.ReadEventsAsync(response);

        // Assert
        events.Select(e => e.Event).ShouldBe(["text", "a2ui", "text"]);
        events[1].Data.ContainsKey("createSurface").ShouldBeTrue();
        errors.Single().Error.ShouldStartWith("The renderer would reject an A2UI message");
    }

    /// <summary>
    /// Proves that a message the renderer would reject is refused with an exception and not sent.
    /// </summary>
    [Fact]
    public async Task SendAsync_RejectedMessage_ThrowsAndSendsNothing()
    {
        // Arrange
        Exception? thrown = null;
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            thrown = await Should.ThrowAsync<A2UIValidationException>(() => turn.SendAsync(A2UIMessages.UpdateDataModel("missing", "/x", 1), ct));
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        thrown.ShouldNotBeNull();
        lines.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a message the renderer would apply in part is not kept in the conversation's mirror, so the agent
    /// can send a corrected one for the same surface.
    /// </summary>
    [Fact]
    public async Task SendAsync_RejectedCreateSurface_CanBeSentAgain()
    {
        // Arrange
        JsonObject broken = A2UIMessages.CreateSurface("main", new Text { Content = "Hi" });

        // The builder always writes the components, so the first one is there.
        broken["createSurface"]!["components"]![0]!["component"] = "Chart";
        Exception? thrown = null;
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            async (turn, ct) =>
            {
                thrown = await Should.ThrowAsync<A2UIValidationException>(() => turn.SendAsync(broken, ct));
                await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hi" }), ct);
            },
            options => options.Validation = A2UIValidationConfig.Strict);

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        thrown.ShouldNotBeNull();
        lines.Single().ContainsKey("createSurface").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that only UI messages can be sent directly.
    /// </summary>
    [Fact]
    public async Task SendAsync_NonUiMessage_Throws()
    {
        // Arrange
        Exception? thrown = null;
        JsonObject message = JsonNode.Parse("""{"version":"1.0","agentFunctionResponse":{"functionCallId":"c1","value":1}}""")!.AsObject();
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            thrown = await Should.ThrowAsync<ArgumentException>(() => turn.SendAsync(message, ct));
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
        await TestAgentHost.ReadLinesAsync(response);

        // Assert
        thrown.ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that the conversation keeps its surfaces between requests, so a later turn can update a surface an
    /// earlier turn created.
    /// </summary>
    [Fact]
    public async Task Post_SameConversation_KeepsSurfacesBetweenTurns()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            JsonObject message = turn.Text == "create"
                ? A2UIMessages.CreateSurface("main", new Text { Content = "Hello" })
                : A2UIMessages.UpdateDataModel("main", "/name", "Ada");
            await turn.SendAsync(message, ct);
        });
        using HttpResponseMessage first = await host.SendAsync(TestAgentHost.Request("""{"text":"create"}"""));
        string id = first.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single();
        await TestAgentHost.ReadLinesAsync(first);

        // Act
        using HttpResponseMessage second = await host.SendAsync(TestAgentHost.Request("""{"text":"update"}""", id));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(second);

        // Assert
        second.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single().ShouldBe(id);
        lines.Single().ContainsKey("updateDataModel").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that an unknown or malformed conversation id is refused with 404.
    /// </summary>
    /// <param name="id">The id the client sends.</param>
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("not-an-id")]
    public async Task Post_UnknownConversation_Returns404(string id)
    {
        // Arrange
        bool ran = false;
        await using TestAgentHost host = await TestAgentHost.StartAsync((_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}""", id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ran.ShouldBeFalse();
    }

    /// <summary>
    /// Proves that malformed requests are refused before anything runs.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="detail">The start of the problem's detail.</param>
    [Theory]
    [InlineData("{", "The body is not valid JSON.")]
    [InlineData("[]", "The body must be a JSON object.")]
    [InlineData("{}", "The request has no text and no messages.")]
    [InlineData("""{"text":1}""", "'text' must be a string.")]
    [InlineData("""{"messages":{}}""", "'messages' must be an array.")]
    [InlineData("""{"text":"a","metadata":[]}""", "'metadata' must be an object.")]
    [InlineData("""{"text":"a","extra":1}""", "The body has the unknown property 'extra'.")]
    [InlineData("""{"text":"a","text":"b"}""", "The body is not valid JSON.")]
    [InlineData("""{"messages":[1]}""", "Message 1 must be a JSON object.")]
    [InlineData("""{"messages":[{"version":"1.0","showChart":{}}]}""", "Message 1 is not a valid renderer message")]
    [InlineData("""{"messages":[{"version":"1.0","action":{"name":"submit"}}]}""", "Message 1 is not a valid renderer message")]
    [InlineData("""{"messages":[{"version":"1.0","callAgentFunction":{"call":"lookup"}}]}""", "Message 1 is not a valid renderer message")]
    [InlineData("""{"messages":[{"version":"1.0","rendererFunctionResponse":{"value":1}}]}""", "Message 1 is not a valid renderer message")]
    [InlineData("""{"text":"\ud800"}""", "The body has a string that is not valid Unicode.")]
    public async Task Post_MalformedBody_Returns400(string body, string detail)
    {
        // Arrange
        bool ran = false;
        await using TestAgentHost host = await TestAgentHost.StartAsync((_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request(body));
        JsonObject problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem["detail"]!.GetValue<string>().ShouldStartWith(detail);
        ran.ShouldBeFalse();
    }

    /// <summary>
    /// Proves that the request limits are enforced: body size, message count and content type.
    /// </summary>
    [Fact]
    public async Task Post_OverLimits_IsRefused()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync((_, _) => Task.CompletedTask, options =>
        {
            options.MaxRequestBodySize = 200;
            options.MaxMessagesPerRequest = 1;
        });
        string error = """{"version":"1.0","error":{"code":"X","message":"m","surfaceId":"s"}}""";
        HttpRequestMessage plain = TestAgentHost.Request("""{"text":"Hi"}""");
        plain.Content = new StringContent("""{"text":"Hi"}""", System.Text.Encoding.UTF8, "text/plain");

        // Act
        using HttpResponseMessage tooLarge = await host.SendAsync(TestAgentHost.Request($$"""{"text":"{{new string('x', 300)}}"}"""));
        using HttpResponseMessage tooMany = await host.SendAsync(TestAgentHost.Request($$"""{"messages":[{{error}},{{error}}]}"""));
        using HttpResponseMessage wrongType = await host.SendAsync(plain);

        // Assert
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrongType.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// Proves that the renderer's actions and errors reach the turn with its metadata.
    /// </summary>
    [Fact]
    public async Task Post_ActionsAndErrors_ReachTheTurn()
    {
        // Arrange
        A2UIAgentTurn? seen = null;
        await using TestAgentHost host = await TestAgentHost.StartAsync((turn, _) =>
        {
            seen = turn;
            return Task.CompletedTask;
        });
        string body = """
            {"messages":[
              {"version":"1.0","action":{"name":"submit","surfaceId":"main","sourceComponentId":"send","timestamp":"2026-11-26T10:15:00Z","context":{"email":"a@b.c"}}},
              {"version":"1.0","error":{"code":"VALIDATION_FAILED","message":"bad","surfaceId":"main","path":"/components/0"}}
            ],"metadata":{"a2uiClientDataModel":{"version":"1.0","surfaces":{}}}}
            """;

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request(body));
        await TestAgentHost.ReadLinesAsync(response);

        // Assert
        seen.ShouldNotBeNull();
        seen.Text.ShouldBeNull();
        seen.Actions.Single().Name.ShouldBe("submit");
        seen.Actions[0].Context["email"]!.GetValue<string>().ShouldBe("a@b.c");
        seen.Errors.Single().Code.ShouldBe("VALIDATION_FAILED");
        seen.Metadata!.ContainsKey("a2uiClientDataModel").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that an agent function call is answered on the response without starting a turn.
    /// </summary>
    [Fact]
    public async Task Post_CallAgentFunction_RepliesWithoutTurn()
    {
        // Arrange
        bool ran = false;
        Catalog tools = ToolsCatalog();
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            (_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            options =>
            {
                options.Catalogs.Add(tools);
                options.AgentFunctions = new A2UIAgentFunctions([tools])
                    .Add(ToolsId, "lookup", (call, _) => ValueTask.FromResult<JsonNode?>("found " + call.Args["key"]!.GetValue<string>()));
            });
        string body = """{"messages":[{"version":"1.0","callAgentFunction":{"surfaceId":"main","functionCallId":"c1","callFunction":{"@call":"lookup","catalogId":"urn:tools","args":{"key":"k1"}}}}]}""";

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request(body));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        ran.ShouldBeFalse();
        JsonNode reply = lines.Single()["agentFunctionResponse"]!;
        reply["functionCallId"]!.GetValue<string>().ShouldBe("c1");
        reply["value"]!.GetValue<string>().ShouldBe("found k1");
    }

    /// <summary>
    /// Proves that a renderer function call made during a turn is answered by a later request of the same
    /// conversation while the turn is still streaming.
    /// </summary>
    [Fact]
    public async Task CallRendererFunctionAsync_ResponseInLaterRequest_ResumesTurn()
    {
        // Arrange
        Catalog tools = ToolsCatalog();
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            async (turn, ct) =>
            {
                JsonNode? city = await turn.CallRendererFunctionAsync(new A2UICall("getCity") { CatalogId = ToolsId }, cancellationToken: ct);
                await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = city!.GetValue<string>() }), ct);
            },
            options => options.Catalogs.Add(tools));
        using HttpResponseMessage first = await host.SendAsync(TestAgentHost.Request("""{"text":"Where am I?"}"""));
        string id = first.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single();
        using StreamReader reader = new(await first.Content.ReadAsStreamAsync());
        JsonObject call = JsonNode.Parse((await reader.ReadLineAsync())!)!.AsObject();
        string functionCallId = call["callRendererFunction"]!["functionCallId"]!.GetValue<string>();

        // Act
        using HttpResponseMessage answer = await host.SendAsync(TestAgentHost.Request(
            $$$"""{"messages":[{"version":"1.0","rendererFunctionResponse":{"functionCallId":"{{{functionCallId}}}","value":"Berlin"}}]}""", id));
        string? next = await reader.ReadLineAsync();

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        next.ShouldNotBeNull();
        next.ShouldContain("Berlin");
        (await reader.ReadLineAsync()).ShouldBeNull();
    }

    /// <summary>
    /// Proves that a renderer function call the renderer would reject, here for a surface that does not exist, is
    /// refused before it is sent and does not stay pending.
    /// </summary>
    [Fact]
    public async Task CallRendererFunctionAsync_UnknownSurface_ThrowsAndSendsNothing()
    {
        // Arrange
        Catalog tools = ToolsCatalog();
        A2UIValidationException? first = null;
        A2UIValidationException? second = null;
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            async (turn, ct) =>
            {
                // Only one call may be pending, so the second is refused the same way only if the first was dropped.
                first = await Should.ThrowAsync<A2UIValidationException>(
                    () => turn.CallRendererFunctionAsync(new A2UICall("getCity") { CatalogId = ToolsId }, "missing", cancellationToken: ct));
                second = await Should.ThrowAsync<A2UIValidationException>(
                    () => turn.CallRendererFunctionAsync(new A2UICall("getCity") { CatalogId = ToolsId }, "missing", cancellationToken: ct));
            },
            options =>
            {
                options.Catalogs.Add(tools);
                options.MaxPendingRendererCalls = 1;
            });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Where am I?"}"""));
        List<JsonObject> lines = await TestAgentHost.ReadLinesAsync(response);

        // Assert
        first.ShouldNotBeNull();
        first.FunctionCallId.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.Code.ShouldBe(first.Code);
        lines.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a second turn in a conversation is refused with 409 while the first is still running.
    /// </summary>
    [Fact]
    public async Task Post_TurnAlreadyRunning_Returns409()
    {
        // Arrange
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        using HttpResponseMessage first = await host.SendAsync(TestAgentHost.Request("""{"text":"one"}"""));
        string id = first.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single();
        await started.Task;

        // Act
        using HttpResponseMessage second = await host.SendAsync(TestAgentHost.Request("""{"text":"two"}""", id));
        release.SetResult();
        await TestAgentHost.ReadLinesAsync(first);

        // Assert
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Proves that a failed turn over server-sent events ends with a generic error event that hides the exception.
    /// </summary>
    [Fact]
    public async Task Post_TurnThrows_EventStreamEndsWithGenericError()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            await turn.SendTextAsync("Working.", ct);
            throw new InvalidOperationException("secret connection string");
        });

        // Act
        using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}""", eventStream: true));
        List<(string Event, JsonObject Data)> events = await TestAgentHost.ReadEventsAsync(response);

        // Assert
        events.Select(e => e.Event).ShouldBe(["text", "error"]);
        events[1].Data["message"]!.GetValue<string>().ShouldBe("The agent could not finish its answer.");
        events[1].Data.ToJsonString().ShouldNotContain("secret");
    }

    /// <summary>
    /// Proves that a failed turn over JSON Lines aborts the connection, so the client never takes a cut-off answer
    /// for a complete one.
    /// </summary>
    [Fact]
    public async Task Post_TurnThrows_JsonLinesConnectionIsAborted()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hello" }), ct);
            throw new InvalidOperationException("boom");
        });

        // Act. The abort can reach the client before the headers or after them, so both are read together.
        Func<Task> read = async () =>
        {
            using HttpResponseMessage response = await host.SendAsync(TestAgentHost.Request("""{"text":"Hi"}"""));
            await TestAgentHost.ReadLinesAsync(response);
        };

        // Assert
        await Should.ThrowAsync<Exception>(read);
    }

    /// <summary>
    /// Proves that no new conversation starts once the store is full.
    /// </summary>
    [Fact]
    public async Task Post_TooManyConversations_Returns503()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync((_, _) => Task.CompletedTask, options => options.MaxConversations = 1);
        using HttpResponseMessage first = await host.SendAsync(TestAgentHost.Request("""{"text":"one"}"""));
        await TestAgentHost.ReadLinesAsync(first);

        // Act
        using HttpResponseMessage second = await host.SendAsync(TestAgentHost.Request("""{"text":"two"}"""));

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Proves that mapping an endpoint without a catalog fails at startup.
    /// </summary>
    [Fact]
    public void MapA2UIAgent_NoCatalog_Throws()
    {
        // Arrange
        Microsoft.AspNetCore.Builder.WebApplication app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();

        // Act
        Action act = () => app.MapA2UIAgent("/a2ui", (_, _) => Task.CompletedTask, _ => { });

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that a renderer function timeout too long for a timer fails at startup.
    /// </summary>
    [Fact]
    public void MapA2UIAgent_TimeoutTooLong_Throws()
    {
        // Arrange
        Microsoft.AspNetCore.Builder.WebApplication app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();

        // Act
        Action act = () => app.MapA2UIAgent("/a2ui", (_, _) => Task.CompletedTask, options =>
        {
            options.Catalogs.Add(BasicCatalog.Create());
            options.RendererFunctionTimeout = TimeSpan.FromDays(60);
        });

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that a conversation belongs to the user who started it: another user gets 404, as for an unknown id.
    /// </summary>
    [Fact]
    public async Task Post_OtherOwner_Returns404()
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            (_, _) => Task.CompletedTask,
            options => options.ConversationOwner = context => context.Request.Headers["X-User"].FirstOrDefault());
        HttpRequestMessage start = TestAgentHost.Request("""{"text":"Hi"}""");
        start.Headers.Add("X-User", "alice");
        using HttpResponseMessage first = await host.SendAsync(start);
        string id = first.Headers.GetValues(A2UIAgentEndpointExtensions.ConversationHeader).Single();
        HttpRequestMessage asBob = TestAgentHost.Request("""{"text":"Hi"}""", id);
        asBob.Headers.Add("X-User", "bob");
        HttpRequestMessage asAlice = TestAgentHost.Request("""{"text":"Hi"}""", id);
        asAlice.Headers.Add("X-User", "alice");

        // Act
        using HttpResponseMessage bob = await host.SendAsync(asBob);
        using HttpResponseMessage alice = await host.SendAsync(asAlice);

        // Assert
        bob.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        alice.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Proves that the <c>Accept</c> header picks the format by quality: server-sent events only when the client
    /// prefers them, never when it gives them a quality of zero.
    /// </summary>
    /// <param name="accept">The <c>Accept</c> header.</param>
    /// <param name="expected">The media type of the response.</param>
    [Theory]
    [InlineData("text/event-stream", "text/event-stream")]
    [InlineData("application/jsonl, text/event-stream;q=0", "application/jsonl")]
    [InlineData("application/jsonl;q=0.5, text/event-stream", "text/event-stream")]
    [InlineData("text/event-stream;q=0.5, application/jsonl", "application/jsonl")]
    [InlineData("text/event-stream;q=0.5, */*;q=0.1", "text/event-stream")]
    [InlineData("*/*", "application/jsonl")]
    public async Task Post_AcceptQuality_PicksFormat(string accept, string expected)
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync((_, _) => Task.CompletedTask);
        HttpRequestMessage request = TestAgentHost.Request("""{"text":"Hi"}""");
        request.Headers.TryAddWithoutValidation("Accept", accept).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await host.SendAsync(request);

        // Assert. A successful response always has a content type.
        response.Content.Headers.ContentType!.MediaType.ShouldBe(expected);
    }

    /// <summary>
    /// Proves that mapping an endpoint with two catalogs that share an id fails at startup.
    /// </summary>
    [Fact]
    public void MapA2UIAgent_DuplicateCatalogIds_Throws()
    {
        // Arrange
        Microsoft.AspNetCore.Builder.WebApplication app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();

        // Act
        Action act = () => app.MapA2UIAgent("/a2ui", (_, _) => Task.CompletedTask, options =>
        {
            options.Catalogs.Add(BasicCatalog.Create());
            options.Catalogs.Add(BasicCatalog.Create());
        });

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("two catalogs");
    }

    /// <summary>
    /// Turns an array into an async stream, as a model client would return chunks.
    /// </summary>
    /// <param name="chunks">The chunks.</param>
    private static async IAsyncEnumerable<string> ToAsync(string[] chunks)
    {
        foreach (string chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    /// <summary>
    /// Builds a catalog with an agent function, <c>lookup</c>, and a renderer function the agent may call,
    /// <c>getCity</c>.
    /// </summary>
    private static Catalog ToolsCatalog()
    {
        JsonObject args = JsonNode.Parse("""{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""")!.AsObject();
        FunctionDefinition[] functions =
        [
            new FunctionDefinition("lookup") { AllowedCallers = AllowedCallers.RendererOrAgent, ArgsSchema = args },
            new FunctionDefinition("getCity") { AllowedCallers = AllowedCallers.RendererOrAgent, ReturnType = "string" },
        ];
        return new Catalog(ToolsId, [], functions, A2UIProtocol.Version);
    }
}
