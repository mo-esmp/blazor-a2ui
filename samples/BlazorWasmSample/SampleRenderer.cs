using BlazorA2UI.BlazorServerSample;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Media;
using BlazorA2UI.SampleAgent;

namespace BlazorA2UI.BlazorWasmSample;

/// <summary>
/// Registers the renderer: the A2UI services with the sample catalog and the media allowlist, and the client that
/// connects them to the agent.
/// </summary>
internal static class SampleRenderer
{
    /// <summary>
    /// Adds the renderer's services: a processor with the basic and sample catalogs, the media allowlist, and an agent
    /// client that streams server-sent events, so the agent's text arrives too, and the chat's transcript, which lasts as
    /// long as the conversation. The client sends with the <see cref="HttpClient"/> registered in the services.
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
        services.AddScoped<ChatTranscript>();
        services.AddA2UIAgentClient(client =>
        {
            client.Endpoint = options.AgentEndpoint;
            client.Format = A2UIAgentStreamFormat.EventStream;
        });
        return services;
    }

    /// <summary>
    /// Implements the sample catalog's <c>getTimeZone</c>: the agent calls it while it books a table. In WebAssembly
    /// the code runs in the browser, and the runtime takes the local time zone from it, so no JavaScript is needed.
    /// </summary>
    /// <returns>The browser's IANA time zone, or null when it has none; the agent then uses UTC.</returns>
    /// <param name="invocation">The call, which has no arguments.</param>
    internal static ValueTask<JsonNode?> GetTimeZoneAsync(FunctionInvocation invocation) =>
        ValueTask.FromResult<JsonNode?>(IanaId(TimeZoneInfo.Local));

    /// <summary>
    /// Gets a time zone's IANA id, converting a Windows id when the app runs on Windows, as it does in tests.
    /// </summary>
    /// <returns>The IANA id, or null when the zone has none, such as a custom zone.</returns>
    /// <param name="zone">The time zone.</param>
    internal static string? IanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? ianaId) ? ianaId : null;
    }
}
