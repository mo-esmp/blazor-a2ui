using BlazorA2UI.Catalogs;
using BlazorA2UI.Client;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.AspNetCore;

/// <summary>
/// Tests the renderer's <see cref="A2UIAgentClient"/> against the real endpoint over HTTP, so the two sides agree on
/// the protocol.
/// </summary>
public sealed class A2UIAgentClientRoundTripTests
{
    /// <summary>
    /// The id of the catalog with a renderer function.
    /// </summary>
    private const string ToolsId = "urn:tools";

    /// <summary>
    /// Proves a whole conversation: a text turn in which the agent calls a renderer function while it streams, then an
    /// action that starts a second turn in the same conversation, which updates the surface the first one created.
    /// </summary>
    /// <param name="format">The format the client asks for.</param>
    [Theory]
    [InlineData(A2UIAgentStreamFormat.JsonLines)]
    [InlineData(A2UIAgentStreamFormat.EventStream)]
    public async Task Conversation_TextThenAction_RoundTrips(A2UIAgentStreamFormat format)
    {
        // Arrange
        int actions = 0;
        await using TestAgentHost host = await TestAgentHost.StartAsync(
            async (turn, ct) =>
            {
                if (turn.Actions.Count > 0)
                {
                    actions += turn.Actions.Count;
                    await turn.SendAsync(A2UIMessages.UpdateDataModel("main", "/status", "sent"), ct);
                    return;
                }

                // The client's renderer answers the call while this response is still open.
                JsonNode? city = await turn.CallRendererFunctionAsync(new A2UICall("getCity") { CatalogId = ToolsId }, cancellationToken: ct);
                await turn.SendTextAsync("Here is the weather.", ct);
                await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Weather" }, dataModel: new JsonObject { ["city"] = city?.DeepClone() }), ct);
            },
            options => options.Catalogs.Add(new Catalog(ToolsId, [], [GetCity()], A2UIProtocol.Version)));
        ValueTask<JsonNode?> Berlin(FunctionInvocation invocation) => ValueTask.FromResult<JsonNode?>("Berlin");
        using MessageProcessor processor = new([BasicCatalog.Create(), new Catalog(ToolsId, [], [GetCity().WithImplementation(Berlin)], A2UIProtocol.Version)]);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions
        {
            // The host always has a base address.
            Endpoint = new Uri(host.Client.BaseAddress!, TestAgentHost.Route),
            Format = format,
        });
        List<string> texts = [];
        client.TextReceived += texts.Add;
        await client.SendTextAsync("Where am I?");
        string? firstConversation = client.ConversationId;
        JsonObject action = JsonNode.Parse("""{"version":"1.0","action":{"name":"submit","surfaceId":"main","sourceComponentId":"root","timestamp":"2026-11-27T10:00:00.000Z","context":{}}}""")!.AsObject();

        // Act
        // The client set the processor's outbound when it was created.
        await processor.Outbound!(new A2UIOutboundMessage(action, null));
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (actions == 0 || client.State != A2UIConnectionState.Ended || processor.Surfaces["main"].DataModel.Get("/status") is null)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, client.LastError);
            await Task.Delay(10);
        }

        // Assert
        client.LastError.ShouldBeNull();
        firstConversation.ShouldNotBeNull();
        client.ConversationId.ShouldBe(firstConversation);

        // The surface exists and holds both turns' data.
        Surface surface = processor.Surfaces["main"];
        surface.DataModel.Get("/city")!.GetValue<string>().ShouldBe("Berlin");
        surface.DataModel.Get("/status")!.GetValue<string>().ShouldBe("sent");
        texts.ShouldBe(format == A2UIAgentStreamFormat.EventStream ? ["Here is the weather."] : []);
        actions.ShouldBe(1);
    }

    /// <summary>
    /// Proves that a turn that fails on the server fails on the client with a clear error in both formats.
    /// </summary>
    /// <param name="format">The format the client asks for.</param>
    /// <param name="error">The error the client reports.</param>
    [Theory]
    [InlineData(A2UIAgentStreamFormat.JsonLines, "The connection to the agent failed.")]
    [InlineData(A2UIAgentStreamFormat.EventStream, "The agent reported an error: The agent could not finish its answer.")]
    public async Task Conversation_TurnThrows_ClientFails(A2UIAgentStreamFormat format, string error)
    {
        // Arrange
        await using TestAgentHost host = await TestAgentHost.StartAsync(async (turn, ct) =>
        {
            await turn.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hello" }), ct);
            throw new InvalidOperationException("boom");
        });
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions
        {
            // The host always has a base address.
            Endpoint = new Uri(host.Client.BaseAddress!, TestAgentHost.Route),
            Format = format,
        });

        // Act
        await client.SendTextAsync("Hi");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe(error);
    }

    /// <summary>
    /// Builds the declaration of the renderer function <c>getCity</c>.
    /// </summary>
    private static FunctionDefinition GetCity() =>
        new("getCity") { AllowedCallers = AllowedCallers.RendererOrAgent, ReturnType = "string" };
}
