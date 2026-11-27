using System.Net.Http.Headers;
using System.Text;
using BlazorA2UI.Catalogs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorA2UI.Tests.AspNetCore;

/// <summary>
/// Runs an agent endpoint on Kestrel at a free local port, so tests exercise the real HTTP pipeline, streaming
/// included, without extra test packages.
/// </summary>
internal sealed class TestAgentHost : IAsyncDisposable
{
    /// <summary>
    /// The route the endpoint is mapped at.
    /// </summary>
    public const string Route = "/a2ui";

    /// <summary>
    /// The running app.
    /// </summary>
    private readonly WebApplication _app;

    /// <summary>
    /// Creates a host around a started app.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <param name="client">A client for the app's address.</param>
    private TestAgentHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>
    /// Gets a client whose base address is the app.
    /// </summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Starts an app with an agent endpoint that uses the basic catalog and any extra options.
    /// </summary>
    /// <returns>The started host.</returns>
    /// <param name="handler">The agent's turn handler.</param>
    /// <param name="configure">Changes the options after the basic catalog is added, or null.</param>
    public static async Task<TestAgentHost> StartAsync(A2UIAgentHandler handler, Action<A2UIAgentEndpointOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapA2UIAgent(Route, handler, options =>
        {
            options.Catalogs.Add(BasicCatalog.Create());
            configure?.Invoke(options);
        });
        await app.StartAsync();

        // Kestrel reports the port it bound.
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        HttpClient client = new() { BaseAddress = new Uri(address) };
        return new TestAgentHost(app, client);
    }

    /// <summary>
    /// Builds a request to the endpoint.
    /// </summary>
    /// <returns>The request.</returns>
    /// <param name="body">The JSON body.</param>
    /// <param name="conversationId">The conversation's id, or null for a new conversation.</param>
    /// <param name="eventStream">Whether to accept server-sent events.</param>
    public static HttpRequestMessage Request(string body, string? conversationId = null, bool eventStream = false)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (conversationId is not null)
        {
            request.Headers.Add(A2UIAgentEndpointExtensions.ConversationHeader, conversationId);
        }

        if (eventStream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        return request;
    }

    /// <summary>
    /// Sends a request and returns once the response headers arrive, leaving the body to stream.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="request">The request.</param>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
        Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>
    /// Reads every line of a JSON Lines response body.
    /// </summary>
    /// <returns>The lines, each parsed.</returns>
    /// <param name="response">The response.</param>
    public static async Task<List<JsonObject>> ReadLinesAsync(HttpResponseMessage response)
    {
        List<JsonObject> lines = [];
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(JsonNode.Parse(line)!.AsObject());
        }

        return lines;
    }

    /// <summary>
    /// Reads every event of a server-sent events response body.
    /// </summary>
    /// <returns>The events' names and data, in order.</returns>
    /// <param name="response">The response.</param>
    public static async Task<List<(string Event, JsonObject Data)>> ReadEventsAsync(HttpResponseMessage response)
    {
        List<(string, JsonObject)> events = [];
        string? name = null;
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                // Every data line in these tests follows its event line.
                events.Add((name!, JsonNode.Parse(line["data: ".Length..])!.AsObject()));
            }
        }

        return events;
    }

    /// <summary>
    /// Stops the app.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
