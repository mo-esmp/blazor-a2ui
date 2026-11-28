using BlazorA2UI.Media;
using BlazorA2UI.SampleAgent;
using Microsoft.Extensions.Configuration;

namespace BlazorA2UI.Tests.BlazorWasmSample;

/// <summary>
/// Tests for <see cref="SampleRenderer"/> and <see cref="SampleRendererOptions"/>.
/// </summary>
public sealed class SampleRendererTests
{
    /// <summary>
    /// Proves that the agent's <c>getTimeZone</c> call is answered with the local time zone, which in WebAssembly is the
    /// browser's.
    /// </summary>
    [Fact]
    public async Task GetTimeZone_AgentCalls_RespondsWithLocalZone()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddScoped(_ => new HttpClient());
        services.AddSampleRenderer(new SampleRendererOptions());
        await using ServiceProvider provider = services.BuildServiceProvider();
        MessageProcessor processor = provider.GetRequiredService<MessageProcessor>();
        List<JsonObject> sent = Record(processor);

        // Act
        processor.Process(TimeZoneCall("c1"));
        await processor.WhenIdleAsync();

        // Assert
        JsonObject response = sent.ShouldHaveSingleItem()["rendererFunctionResponse"].ShouldBeOfType<JsonObject>();
        response["functionCallId"]!.GetValue<string>().ShouldBe("c1");
        response.ContainsKey("error").ShouldBeFalse();
        string? zone = response["value"]?.GetValue<string>();
        zone.ShouldBe(SampleRenderer.IanaId(TimeZoneInfo.Local));
    }

    /// <summary>
    /// Proves that a zone with an IANA id answers with that id.
    /// </summary>
    [Fact]
    public void IanaId_IanaZone_ReturnsId()
    {
        // Arrange
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

        // Act
        string? id = SampleRenderer.IanaId(zone);

        // Assert
        id.ShouldBe("Europe/Berlin");
    }

    /// <summary>
    /// Proves that a zone without an IANA id answers null, which the agent turns into UTC, instead of a made-up id.
    /// </summary>
    [Fact]
    public void IanaId_CustomZone_ReturnsNull()
    {
        // Arrange
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Sample Standard Time", TimeSpan.FromHours(3), "Sample", "Sample");

        // Act
        string? id = SampleRenderer.IanaId(zone);

        // Assert
        id.ShouldBeNull();
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
        IA2UIMediaResolver resolver = provider.GetRequiredService<IA2UIMediaResolver>();

        // Act
        string? image = resolver.Resolve(new A2UIMediaRequest(A2UIMediaKind.Image, "https://cdn.test/media/a.png", "s", "c"))?.ToString();
        string? video = resolver.Resolve(new A2UIMediaRequest(A2UIMediaKind.Video, "https://cdn.test/media/a.mp4", "s", "c"))?.ToString();
        string? elsewhere = resolver.Resolve(new A2UIMediaRequest(A2UIMediaKind.Image, "https://other.test/a.png", "s", "c"))?.ToString();

        // Assert
        image.ShouldBe("https://cdn.test/media/a.png");
        video.ShouldBeNull();
        elsewhere.ShouldBeNull();
    }

    /// <summary>
    /// Proves that the agent's endpoint and the media prefixes are read from the app's configuration.
    /// </summary>
    [Fact]
    public void Load_Settings_ReadsEndpointAndPrefixes()
    {
        // Arrange
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["BlazorWasmSample:AgentEndpoint"] = "https://agent.test/a2ui",
            ["BlazorWasmSample:MediaPrefixes:0"] = "https://cdn.test/media/",
        });

        // Act
        SampleRendererOptions options = SampleRendererOptions.Load(configuration);

        // Assert
        options.AgentEndpoint.ShouldBe(new Uri("https://agent.test/a2ui"));
        options.MediaPrefixes.ShouldBe(["https://cdn.test/media/"]);
    }

    /// <summary>
    /// Proves that a configuration without the section gives the defaults, which call the sample agent's local URL.
    /// </summary>
    [Fact]
    public void Load_NoSection_UsesDefaults()
    {
        // Arrange
        IConfiguration configuration = Configuration([]);

        // Act
        SampleRendererOptions options = SampleRendererOptions.Load(configuration);

        // Assert
        options.AgentEndpoint.ShouldBe(new Uri("http://localhost:5080/a2ui"));
        options.MediaPrefixes.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that an agent endpoint that is not an absolute http or https URL stops the app at startup.
    /// </summary>
    /// <param name="endpoint">The configured endpoint.</param>
    [Theory]
    [InlineData("/a2ui")]
    [InlineData("ftp://agent.test/a2ui")]
    public void Load_InvalidEndpoint_Throws(string endpoint)
    {
        // Arrange
        IConfiguration configuration = Configuration(new Dictionary<string, string?> { ["BlazorWasmSample:AgentEndpoint"] = endpoint });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => SampleRendererOptions.Load(configuration));

        // Assert
        error.Message.ShouldContain("AgentEndpoint");
    }

    /// <summary>
    /// Builds a configuration from settings.
    /// </summary>
    /// <returns>The configuration.</returns>
    /// <param name="settings">The settings, by key.</param>
    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

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
