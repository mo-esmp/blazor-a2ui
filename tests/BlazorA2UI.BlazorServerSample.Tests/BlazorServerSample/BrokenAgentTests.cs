using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace BlazorA2UI.Tests.BlazorServerSample;

/// <summary>
/// Tests for <see cref="BrokenAgent"/>, through the sample app on Kestrel and the library's client, as the Errors page
/// uses them.
/// </summary>
public sealed class BrokenAgentTests
{
    /// <summary>
    /// Proves that each kind of break fails the turn with its own clear error.
    /// </summary>
    /// <param name="mode">How the answer breaks.</param>
    /// <param name="expected">The error the client reports.</param>
    [Theory]
    [InlineData(BrokenAgent.Cut, "The agent's stream ended in the middle of a message.")]
    [InlineData(BrokenAgent.Drop, "The connection to the agent failed.")]
    public async Task Turn_BrokenAnswer_FailsWithClearError(string mode, string expected)
    {
        // Arrange
        await using WebApplication app = await StartAsync();
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = new Uri(Address(app), $"broken-agent/{mode}") });

        // Act
        await client.SendTextAsync("Hello");

        // Assert
        client.State.ShouldBe(A2UIConnectionState.Failed);
        client.LastError.ShouldBe(expected);
    }

    /// <summary>
    /// Proves that the complete message before a cut is processed. A dropped connection gives no such promise, because
    /// the reset can discard bytes the client has not read yet.
    /// </summary>
    [Fact]
    public async Task Turn_CutAnswer_KeepsCompleteMessage()
    {
        // Arrange
        await using WebApplication app = await StartAsync();
        using MessageProcessor processor = new([BasicCatalog.Create()]);
        using HttpClient http = new();
        await using A2UIAgentClient client = new(http, processor, new A2UIAgentClientOptions { Endpoint = new Uri(Address(app), $"broken-agent/{BrokenAgent.Cut}") });

        // Act
        await client.SendTextAsync("Hello");

        // Assert
        processor.Surfaces.ContainsKey(BrokenAgent.SurfaceId).ShouldBeTrue();
    }

    /// <summary>
    /// Proves that only the two kinds of break are served.
    /// </summary>
    [Fact]
    public async Task Post_UnknownMode_IsNotFound()
    {
        // Arrange
        await using WebApplication app = await StartAsync();
        using HttpClient http = new();

        // Act
        using HttpResponseMessage response = await http.PostAsync(new Uri(Address(app), "broken-agent/other"), new StringContent("{}"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Starts the sample app on a free local port. The app is named as the sample, so it finds its static assets'
    /// manifest next to the test assembly.
    /// </summary>
    /// <returns>The started app.</returns>
    private static async Task<WebApplication> StartAsync()
    {
        WebApplication app = BlazorServerSampleApp.Create(
            ["--urls=http://127.0.0.1:0", "--environment=Production", "--applicationName=BlazorServerSample"],
            services => services.AddLogging(logging => logging.ClearProviders()));
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// Returns the address Kestrel bound.
    /// </summary>
    /// <returns>The address.</returns>
    /// <param name="app">The started app.</param>
    private static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First() + "/");
}
