using BlazorA2UI.SampleAgent;

namespace BlazorA2UI.Tests.BlazorServerSample;

/// <summary>
/// Tests for <see cref="SampleRenderer"/>.
/// </summary>
public sealed class SampleRendererTests
{
    /// <summary>
    /// Proves that the agent's <c>getTimeZone</c> call is answered with the browser's time zone.
    /// </summary>
    [Fact]
    public async Task GetTimeZone_BrowserAnswers_RespondsWithZone()
    {
        // Arrange
        await using BunitContext context = new();
        context.JSInterop.Setup<string?>(SampleRenderer.TimeZoneFunction).SetResult("Europe/Berlin");
        context.Services.AddSampleRenderer(new SampleRendererOptions());
        MessageProcessor processor = context.Services.GetRequiredService<MessageProcessor>();
        List<JsonObject> sent = Record(processor);

        // Act
        processor.Process(TimeZoneCall("c1"));
        await processor.WhenIdleAsync();

        // Assert
        JsonObject response = sent.ShouldHaveSingleItem()["rendererFunctionResponse"].ShouldBeOfType<JsonObject>();
        response["functionCallId"]!.GetValue<string>().ShouldBe("c1");
        response["value"]!.GetValue<string>().ShouldBe("Europe/Berlin");
    }

    /// <summary>
    /// Proves that a browser that cannot tell its time zone gets a null answer, which the agent turns into UTC,
    /// instead of an error.
    /// </summary>
    [Fact]
    public async Task GetTimeZone_BrowserFails_RespondsWithNull()
    {
        // Arrange
        await using BunitContext context = new();
        context.JSInterop.Setup<string?>(SampleRenderer.TimeZoneFunction).SetException(new Microsoft.JSInterop.JSException("No Intl."));
        context.Services.AddSampleRenderer(new SampleRendererOptions());
        MessageProcessor processor = context.Services.GetRequiredService<MessageProcessor>();
        List<JsonObject> sent = Record(processor);

        // Act
        processor.Process(TimeZoneCall("c2"));
        await processor.WhenIdleAsync();

        // Assert
        JsonObject response = sent.ShouldHaveSingleItem()["rendererFunctionResponse"].ShouldBeOfType<JsonObject>();
        response.ContainsKey("error").ShouldBeFalse();
        response["value"].ShouldBeNull();
    }

    /// <summary>
    /// Proves that the media allowlist comes from the settings and allows only images.
    /// </summary>
    [Fact]
    public void AddSampleRenderer_MediaPrefixes_AllowOnlyThoseImages()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddSampleRenderer(new SampleRendererOptions { MediaPrefixes = ["https://cdn.test/media/"] });
        using ServiceProvider provider = services.BuildServiceProvider();

        // The resolver is a singleton, so no scoped client is created and the provider can be disposed synchronously.
        BlazorA2UI.Media.IA2UIMediaResolver resolver = provider.GetRequiredService<BlazorA2UI.Media.IA2UIMediaResolver>();

        // Act
        string? image = resolver.Resolve(new BlazorA2UI.Media.A2UIMediaRequest(BlazorA2UI.Media.A2UIMediaKind.Image, "https://cdn.test/media/a.png", "s", "c"))?.ToString();
        string? video = resolver.Resolve(new BlazorA2UI.Media.A2UIMediaRequest(BlazorA2UI.Media.A2UIMediaKind.Video, "https://cdn.test/media/a.mp4", "s", "c"))?.ToString();
        string? elsewhere = resolver.Resolve(new BlazorA2UI.Media.A2UIMediaRequest(BlazorA2UI.Media.A2UIMediaKind.Image, "https://other.test/a.png", "s", "c"))?.ToString();

        // Assert
        image.ShouldBe("https://cdn.test/media/a.png");
        video.ShouldBeNull();
        elsewhere.ShouldBeNull();
    }

    /// <summary>
    /// Proves that an agent endpoint that is not an absolute http or https URL stops the app at startup.
    /// </summary>
    [Fact]
    public void Validate_RelativeEndpoint_Throws()
    {
        // Arrange
        SampleRendererOptions options = new() { AgentEndpoint = new Uri("/a2ui", UriKind.Relative) };

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(options.Validate);

        // Assert
        error.Message.ShouldContain("AgentEndpoint");
    }

    /// <summary>
    /// Builds the agent's <c>getTimeZone</c> call.
    /// </summary>
    /// <returns>The message.</returns>
    /// <param name="functionCallId">The call's id.</param>
    private static JsonObject TimeZoneCall(string functionCallId) => new()
    {
        ["version"] = "1.0",
        ["callRendererFunction"] = new JsonObject
        {
            ["functionCallId"] = functionCallId,
            ["callFunction"] = new JsonObject { ["@call"] = SampleCatalog.GetTimeZone, ["catalogId"] = SampleCatalog.CatalogId },
        },
    };

    /// <summary>
    /// Records the messages the processor sends.
    /// </summary>
    /// <returns>The messages, in order.</returns>
    /// <param name="processor">The processor.</param>
    private static List<JsonObject> Record(MessageProcessor processor)
    {
        List<JsonObject> sent = [];
        processor.Outbound = message =>
        {
            lock (sent)
            {
                sent.Add(message.Message);
            }

            return ValueTask.CompletedTask;
        };
        return sent;
    }
}
