using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Runs the sample agent on Kestrel at a free local port, offline unless a test supplies its own chat client.
/// </summary>
internal sealed class SampleAgentHost : IAsyncDisposable
{
    /// <summary>
    /// The renderer origin the tests allow.
    /// </summary>
    public const string RendererOrigin = "https://renderer.test";

    /// <summary>
    /// The running app.
    /// </summary>
    private readonly WebApplication _app;

    /// <summary>
    /// Creates a host around a started app.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <param name="address">The app's address.</param>
    private SampleAgentHost(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>
    /// Gets the app's address.
    /// </summary>
    public Uri Address { get; }

    /// <summary>
    /// Gets the agent endpoint's address.
    /// </summary>
    public Uri Endpoint => new(Address, SampleAgentApp.Route);

    /// <summary>
    /// Starts the sample with a fixed clock and the test origin.
    /// </summary>
    /// <returns>The started host.</returns>
    /// <param name="today">The date the agent's clock shows.</param>
    /// <param name="chat">The chat client to use, or null for the offline script.</param>
    public static async Task<SampleAgentHost> StartAsync(DateOnly today, IChatClient? chat = null)
    {
        string[] args =
        [
            "--urls=http://127.0.0.1:0",
            "--environment=Production",
            $"--{SampleAgentOptions.Section}:RendererOrigins:0={RendererOrigin}",
        ];
        WebApplication app = SampleAgentApp.Create(args, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<TimeProvider>(new FixedTime(today));
            if (chat is not null)
            {
                services.AddSingleton(chat);
            }
        });
        await app.StartAsync();

        // Kestrel reports the port it bound.
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new SampleAgentHost(app, new Uri(address));
    }

    /// <summary>
    /// Stops the app.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// A clock stopped at noon UTC on one day.
    /// </summary>
    /// <param name="today">The day.</param>
    private sealed class FixedTime(DateOnly today) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }
}
