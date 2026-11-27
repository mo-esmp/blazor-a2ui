using System.Net;
using System.Runtime.CompilerServices;
using BlazorA2UI.Client;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Tests the sample agent over HTTP with the library's own client and processor, as a renderer app uses them.
/// </summary>
public sealed class SampleAgentAppTests
{
    /// <summary>
    /// The day the agent's clock shows, a Friday.
    /// </summary>
    private static readonly DateOnly s_today = new(2026, 11, 27);

    /// <summary>
    /// Proves the whole booking offline: the user's text brings the form, its availability line calls the agent
    /// function, and the <c>book</c> action is handled end to end, with the agent asking the renderer for the time zone.
    /// </summary>
    /// <param name="format">The format the client asks for.</param>
    [Theory]
    [InlineData(A2UIAgentStreamFormat.JsonLines)]
    [InlineData(A2UIAgentStreamFormat.EventStream)]
    public async Task Booking_Offline_ConfirmsInRendererTimeZone(A2UIAgentStreamFormat format)
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using MessageProcessor processor = Renderer(timeZone: "Europe/Berlin");
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint, Format = format });
        await client.SendTextAsync("I'd like to book a table");
        client.LastError.ShouldBeNull();
        JsonNode? availability = await processor.CallAgentFunctionAsync(
            BookingSurfaces.BookingId,
            SampleCatalog.CatalogId,
            SampleCatalog.CheckAvailability,
            new JsonObject { ["date"] = "2026-11-28", ["guests"] = 4 });

        // Act
        await SendBookAsync(processor, new JsonObject { ["name"] = "Ada", ["date"] = "2026-11-28", ["guests"] = 4 });
        await WaitForAsync(client, () => Read(processor, "/confirmation/timeZone") is not null);

        // Assert
        availability!.GetValue<string>().ShouldBe("A table is free. Press Book to reserve it.");
        Read(processor, "/confirmation/timeZone").ShouldBe("Europe/Berlin");
        Read(processor, "/confirmation/name").ShouldBe("Ada");
        Read(processor, "/confirmation/date").ShouldBe("2026-11-28");
        client.LastError.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a booking the rules refuse shows the reason on the form and books nothing.
    /// </summary>
    [Fact]
    public async Task Book_ClosedDay_ShowsReasonOnForm()
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using MessageProcessor processor = Renderer(timeZone: "Europe/Berlin");
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint });
        await client.SendTextAsync("Book a table");

        // Act
        await SendBookAsync(processor, new JsonObject { ["name"] = "Ada", ["date"] = "2026-11-30", ["guests"] = 2 });
        await WaitForAsync(client, () => Read(processor, BookingSurfaces.ProblemPath) is { Length: > 0 });

        // Assert
        Read(processor, BookingSurfaces.ProblemPath).ShouldBe("The restaurant is closed on Mondays.");
        Read(processor, "/confirmation/name").ShouldBeNull();
    }

    /// <summary>
    /// Proves that a renderer that cannot tell the time zone still gets its booking, in UTC.
    /// </summary>
    [Fact]
    public async Task Book_RendererWithoutTimeZone_FallsBackToUtc()
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using MessageProcessor processor = Renderer(timeZone: null);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint });
        await client.SendTextAsync("Book a table");

        // Act
        await SendBookAsync(processor, new JsonObject { ["name"] = "Ada", ["date"] = "2026-11-28", ["guests"] = 2 });
        await WaitForAsync(client, () => Read(processor, "/confirmation/timeZone") is not null);

        // Assert
        Read(processor, "/confirmation/timeZone").ShouldBe("UTC");
    }

    /// <summary>
    /// Proves that the welcome's button starts a booking through the model's turn, like typing the request.
    /// </summary>
    [Fact]
    public async Task StartBooking_Action_ShowsForm()
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using MessageProcessor processor = Renderer(timeZone: null);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint });
        await client.SendTextAsync("Hello");
        processor.Surfaces.ContainsKey(BookingSurfaces.WelcomeId).ShouldBeTrue();
        JsonObject action = new()
        {
            ["version"] = "1.0",
            ["action"] = new JsonObject
            {
                ["name"] = BookingSurfaces.StartBookingAction,
                ["surfaceId"] = BookingSurfaces.WelcomeId,
                ["sourceComponentId"] = "root__button_2",
                ["timestamp"] = "2026-11-27T12:00:00.000Z",
                ["context"] = new JsonObject(),
            },
        };

        // Act
        // The client set the processor's outbound when it was created.
        await processor.Outbound!(new A2UIOutboundMessage(action, null));
        await WaitForAsync(client, () => processor.Surfaces.ContainsKey(BookingSurfaces.BookingId));

        // Assert
        client.LastError.ShouldBeNull();
    }

    /// <summary>
    /// Proves that an answer with a message the renderer would reject is held back and handed to the model with the
    /// reason, once, and that the corrected answer reaches the renderer.
    /// </summary>
    [Fact]
    public async Task Text_RejectedMessage_ModelRepairsOnce()
    {
        // Arrange
        string broken = "Here.\n" + BookingSurfaces.ToBlock([A2UIMessages.UpdateDataModel("nowhere", "/x", 1)]);
        RecordingChatClient chat = new(broken, ScriptedChatClient.s_welcomeAnswer);
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today, chat);
        using MessageProcessor processor = Renderer(timeZone: null);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint });

        // Act
        await client.SendTextAsync("Hello");

        // Assert
        client.LastError.ShouldBeNull();
        processor.Surfaces.ContainsKey(BookingSurfaces.WelcomeId).ShouldBeTrue();
        chat.Requests.Count.ShouldBe(2);
        ChatMessage repair = chat.Requests[1][^1];
        repair.Role.ShouldBe(ChatRole.User);
        repair.Text.ShouldStartWith(BookingAgent.RepairPrompt);
        chat.Requests[1][0].Text.ShouldBe(BookingAgent.SystemPrompt);
    }

    /// <summary>
    /// Proves that the model sees the conversation so far on the next turn.
    /// </summary>
    [Fact]
    public async Task Text_SecondTurn_SendsHistory()
    {
        // Arrange
        RecordingChatClient chat = new(ScriptedChatClient.s_welcomeAnswer, "Sure.");
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today, chat);
        using MessageProcessor processor = Renderer(timeZone: null);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = host.Endpoint });
        await client.SendTextAsync("Hello");

        // Act
        await client.SendTextAsync("Thanks");

        // Assert
        chat.Requests[1].Select(m => m.Text).ShouldBe([BookingAgent.SystemPrompt, "Hello", ScriptedChatClient.s_welcomeAnswer, "Thanks"]);
    }

    /// <summary>
    /// Proves that a browser on the renderer's origin may call the agent and read the conversation header, and that
    /// another origin may not.
    /// </summary>
    /// <param name="origin">The page's origin.</param>
    /// <param name="allowed">Whether the origin is allowed.</param>
    [Theory]
    [InlineData(SampleAgentHost.RendererOrigin, true)]
    [InlineData("https://elsewhere.test", false)]
    public async Task Cors_Preflight_AllowsOnlyRendererOrigin(string origin, bool allowed)
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using HttpClient http = new();
        using HttpRequestMessage preflight = new(HttpMethod.Options, host.Endpoint);
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,a2ui-conversation");

        // Act
        using HttpResponseMessage response = await http.SendAsync(preflight);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? origins).ShouldBe(allowed);
        if (allowed)
        {
            origins.ShouldBe([origin]);
        }
    }

    /// <summary>
    /// Proves that the endpoint's answer exposes the conversation header to the renderer's page.
    /// </summary>
    [Fact]
    public async Task Cors_Post_ExposesConversationHeader()
    {
        // Arrange
        await using SampleAgentHost host = await SampleAgentHost.StartAsync(s_today);
        using HttpClient http = new();
        using HttpRequestMessage request = new(HttpMethod.Post, host.Endpoint)
        {
            Content = new StringContent("""{"text":"Hello"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", SampleAgentHost.RendererOrigin);

        // Act
        using HttpResponseMessage response = await http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Expose-Headers").ShouldContain(A2UIAgentEndpointExtensions.ConversationHeader);
    }

    /// <summary>
    /// Proves that the sample uses the offline script without an API key and the configured model with one.
    /// </summary>
    [Fact]
    public void CreateChatClient_ApiKey_ChoosesModel()
    {
        // Act
        using IChatClient offline = SampleAgentApp.CreateChatClient(new SampleModelOptions());
        using IChatClient model = SampleAgentApp.CreateChatClient(new SampleModelOptions { ApiKey = "test-key", Name = "test-model", Endpoint = new Uri("http://localhost:11434/v1") });

        // Assert
        offline.ShouldBeOfType<ScriptedChatClient>();
        model.ShouldNotBeOfType<ScriptedChatClient>();
        model.GetService<ChatClientMetadata>().ShouldNotBeNull().DefaultModelId.ShouldBe("test-model");
    }

    /// <summary>
    /// Proves that settings a browser could not use stop the app at startup.
    /// </summary>
    /// <param name="origin">The origin setting.</param>
    [Theory]
    [InlineData("localhost:7090")]
    [InlineData("https://localhost:7090/")]
    [InlineData("https://localhost:7090/app")]
    [InlineData("ftp://localhost")]
    public void Validate_BadOrigin_Throws(string origin)
    {
        // Arrange
        SampleAgentOptions options = new() { RendererOrigins = [origin] };

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(options.Validate);

        // Assert
        error.Message.ShouldContain(origin);
    }

    /// <summary>
    /// Builds a renderer's processor with the basic catalog and the sample catalog.
    /// </summary>
    /// <returns>The processor.</returns>
    /// <param name="timeZone">What <c>getTimeZone</c> returns, or null for a renderer that does not implement it.</param>
    private static MessageProcessor Renderer(string? timeZone)
    {
        Catalog catalog = SampleCatalog.Create();
        if (timeZone is not null)
        {
            catalog = catalog.WithFunctions(new Dictionary<string, A2UIFunction>
            {
                [SampleCatalog.GetTimeZone] = _ => ValueTask.FromResult<JsonNode?>(timeZone),
            });
        }

        return new MessageProcessor([BasicCatalog.Create(), catalog]);
    }

    /// <summary>
    /// Sends the form's <c>book</c> action, as the renderer does when the user presses Book.
    /// </summary>
    /// <param name="processor">The processor.</param>
    /// <param name="context">The action's context.</param>
    private static async Task SendBookAsync(MessageProcessor processor, JsonObject context)
    {
        JsonObject action = new()
        {
            ["version"] = "1.0",
            ["action"] = new JsonObject
            {
                ["name"] = BookingSurfaces.BookAction,
                ["surfaceId"] = BookingSurfaces.BookingId,
                ["sourceComponentId"] = "root__button_7",
                ["timestamp"] = "2026-11-27T12:00:00.000Z",
                ["context"] = context,
            },
        };

        // The client set the processor's outbound when it was created.
        await processor.Outbound!(new A2UIOutboundMessage(action, null));
    }

    /// <summary>
    /// Reads a string from the booking surface's data model.
    /// </summary>
    /// <returns>The string, or null when the surface or the value is missing.</returns>
    /// <param name="processor">The processor.</param>
    /// <param name="path">The value's path.</param>
    private static string? Read(MessageProcessor processor, string path) =>
        processor.Surfaces.TryGetValue(BookingSurfaces.BookingId, out Surface? surface) ? A2UIValues.AsString(surface.DataModel.Get(path)) : null;

    /// <summary>
    /// Waits until a condition holds and the client's turns are over, for at most ten seconds.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="condition">The condition.</param>
    private static async Task WaitForAsync(A2UIAgentClient client, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() || client.State is A2UIConnectionState.Connecting or A2UIConnectionState.Streaming)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, client.LastError);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A chat client that gives set answers in order and records what it was sent.
    /// </summary>
    /// <param name="answers">The answers, in order; the last repeats.</param>
    private sealed class RecordingChatClient(params string[] answers) : IChatClient
    {
        /// <summary>
        /// Gets the messages of each request, in order.
        /// </summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Next(messages))));

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string answer = Next(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <summary>
        /// Releases nothing.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Records a request and returns its answer.
        /// </summary>
        /// <returns>The answer.</returns>
        /// <param name="messages">The request's messages.</param>
        private string Next(IEnumerable<ChatMessage> messages)
        {
            lock (Requests)
            {
                Requests.Add([.. messages]);
                return answers[Math.Min(Requests.Count, answers.Length) - 1];
            }
        }
    }
}
