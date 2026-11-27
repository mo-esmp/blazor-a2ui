namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Tests for <see cref="ScriptedChatClient"/>.
/// </summary>
public sealed class ScriptedChatClientTests
{
    /// <summary>
    /// Proves that the script streams its answer in several chunks that add up to the whole answer.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_BookingRequest_StreamsFormInChunks()
    {
        // Arrange
        using ScriptedChatClient client = new();
        List<string> chunks = [];

        // Act
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Can I book a table?")]))
        {
            chunks.Add(update.Text);
        }

        // Assert
        chunks.Count.ShouldBeGreaterThan(1);
        string.Concat(chunks).ShouldBe(ScriptedChatClient.s_bookingAnswer);
    }

    /// <summary>
    /// Proves that the answer without streaming is the same answer.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_Greeting_ReturnsWelcome()
    {
        // Arrange
        using ScriptedChatClient client = new();

        // Act
        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        // Assert
        response.Text.ShouldBe(ScriptedChatClient.s_welcomeAnswer);
    }

    /// <summary>
    /// Proves that every scripted answer reads back through the parser into messages a renderer with the sample's
    /// catalogs accepts, in order, so offline mode exercises the real path.
    /// </summary>
    /// <param name="userText">What the user says.</param>
    /// <param name="messageCount">How many A2UI messages the answer holds.</param>
    [Theory]
    [InlineData("Book a table", 2)]
    [InlineData("Hello", 2)]
    [InlineData(BookingAgent.RepairPrompt + "\n- something", 0)]
    public void Answer_EveryAnswer_ParsesAndValidates(string userText, int messageCount)
    {
        // Arrange
        string answer = ScriptedChatClient.Answer([new ChatMessage(ChatRole.User, userText)]);
        using A2UIMessageValidator validator = new([BasicCatalog.Create(), SampleCatalog.Create()]);

        // Act
        IReadOnlyList<A2UIResponsePart> parts = A2UIResponseParser.Parse(answer);

        // Assert
        parts.OfType<A2UIErrorPart>().ShouldBeEmpty();
        List<JsonObject> messages = [.. parts.OfType<A2UIMessagePart>().Select(p => p.Message)];
        messages.Count.ShouldBe(messageCount);
        foreach (JsonObject message in messages)
        {
            A2UIValidationResult result = validator.Validate(message);
            result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Path}: {e.Message}")));
        }
    }

    /// <summary>
    /// Proves that the client offers itself as a service and nothing else.
    /// </summary>
    [Fact]
    public void GetService_ItsOwnType_ReturnsItself()
    {
        // Arrange
        using ScriptedChatClient client = new();

        // Act
        object? self = client.GetService(typeof(IChatClient));
        object? keyed = client.GetService(typeof(IChatClient), "key");
        object? other = client.GetService(typeof(ChatClientMetadata));

        // Assert
        self.ShouldBeSameAs(client);
        keyed.ShouldBeNull();
        other.ShouldBeNull();
    }
}
