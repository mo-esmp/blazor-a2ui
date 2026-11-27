namespace BlazorA2UI.Tests.BlazorServerSample;

/// <summary>
/// Tests for <see cref="BrokenAgent"/>, through the library's client, as the Errors page uses it.
/// </summary>
public sealed class BrokenAgentTests
{
    /// <summary>
    /// Proves that each kind of break fails the turn with its own clear error, after the complete message before it
    /// was processed.
    /// </summary>
    /// <param name="mode">How the answer breaks.</param>
    /// <param name="expected">The error the client reports.</param>
    [Theory]
    [InlineData(BrokenAgent.Cut, "The agent's stream ended in the middle of a message.")]
    [InlineData(BrokenAgent.Drop, "The connection to the agent failed.")]
    public async Task Turn_BrokenAnswer_FailsWithClearError(string mode, string expected)
    {
        // Arrange
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        using HttpClient http = new(new BrokenAgent(mode));
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = BrokenAgent.Endpoint });

        // Act
        await client.SendTextAsync("Hello");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe(expected);
        processor.Surfaces.ContainsKey(BrokenAgent.SurfaceId).ShouldBeTrue();
    }

    /// <summary>
    /// Proves that only the two kinds of break are accepted.
    /// </summary>
    [Fact]
    public void Constructor_UnknownMode_Throws()
    {
        // Arrange
        const string mode = "other";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new BrokenAgent(mode));

        // Assert
        exception.ParamName.ShouldBe("mode");
    }
}
