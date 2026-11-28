using BlazorA2UI.Evaluation;
using BlazorA2UI.Media;
using BlazorA2UI.SampleAgent;
using Microsoft.JSInterop;

namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// Registers the renderer: the A2UI services with the sample catalog and the media allowlist, and the client that
/// connects them to the agent.
/// </summary>
internal static class SampleRenderer
{
    /// <summary>
    /// The name of the <see cref="HttpClient"/> the agent client uses.
    /// </summary>
    public const string HttpClientName = "agent";

    /// <summary>
    /// The JavaScript function, in <c>wwwroot/js/sample.js</c>, that returns the browser's time zone.
    /// </summary>
    public const string TimeZoneFunction = "blazorA2UISample.timeZone";

    /// <summary>
    /// Adds the renderer's services for each circuit: a processor with the basic and sample catalogs, the media
    /// allowlist, an agent client that streams server-sent events, so the agent's text arrives too, and the chat's
    /// transcript, which lasts as long as the conversation.
    /// </summary>
    /// <returns>The services, for chaining.</returns>
    /// <param name="services">The services.</param>
    /// <param name="options">The sample's settings.</param>
    public static IServiceCollection AddSampleRenderer(this IServiceCollection services, SampleRendererOptions options)
    {
        services.AddA2UI(a2ui =>
        {
            foreach (string prefix in options.MediaPrefixes)
            {
                a2ui.Media.Allow(prefix, A2UIMediaKind.Image);
            }

            a2ui.Catalogs.Add(SampleCatalog.Create().WithFunctions(new Dictionary<string, A2UIFunction>
            {
                [SampleCatalog.GetTimeZone] = GetTimeZoneAsync,
            }));
        });
        services.AddHttpClient(HttpClientName);
        services.AddScoped<ChatTranscript>();
        services.AddA2UIAgentClient(
            client =>
            {
                client.Endpoint = options.AgentEndpoint;
                client.Format = A2UIAgentStreamFormat.EventStream;
            },
            provider => provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName));
        return services;
    }

    /// <summary>
    /// Implements the sample catalog's <c>getTimeZone</c>: the agent calls it while it books a table. In Blazor Server
    /// the code runs on the server, so it asks the user's browser through JavaScript interop.
    /// </summary>
    /// <returns>The browser's IANA time zone, or null when the browser cannot say; the agent then uses UTC.</returns>
    /// <param name="invocation">The call.</param>
    internal static async ValueTask<JsonNode?> GetTimeZoneAsync(FunctionInvocation invocation)
    {
        if (invocation.Services?.GetService<IJSRuntime>() is not { } js)
        {
            return null;
        }

        try
        {
            return await js.InvokeAsync<string?>(TimeZoneFunction, invocation.CancellationToken);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }
}
