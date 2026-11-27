using System.Net;
using System.Text;
using BlazorA2UI.BlazorServerSample.Components.Pages;
using Microsoft.Extensions.Http;

namespace BlazorA2UI.Tests.BlazorServerSample.Components.Pages;

/// <summary>
/// Tests for the <see cref="Chat"/> page against an agent in memory.
/// </summary>
public sealed class ChatTests
{
    /// <summary>
    /// A message that creates a surface with a button whose action goes to the agent.
    /// </summary>
    private const string WelcomeSurface = """{"version":"1.0","createSurface":{"surfaceId":"welcome","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","components":[{"id":"root","component":"Button","child":"label","action":{"event":{"name":"start"}}},{"id":"label","component":"Text","text":"Book a table"}]}}""";

    /// <summary>
    /// A message that creates a surface with one text component.
    /// </summary>
    private const string CreateSurface = """{"version":"1.0","createSurface":{"surfaceId":"booking","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","components":[{"id":"root","component":"Text","text":"Book a table"}]}}""";

    /// <summary>
    /// Proves that sending text shows it, streams the agent's text into the transcript as text, and shows the surface
    /// the agent creates.
    /// </summary>
    [Fact]
    public async Task Send_AgentAnswers_ShowsTextAndSurface()
    {
        // Arrange
        FakeAgentHandler agent = new(() => FakeAgentHandler.EventStream("Let's find you <b>a table</b>.", CreateSurface));
        await using BunitContext context = Context(agent);
        IRenderedComponent<Chat> page = context.Render<Chat>();

        // Act
        page.Find("input[aria-label=Message]").Change("Book a table");
        page.Find("form").Submit();

        // Assert
        page.WaitForAssertion(() =>
        {
            page.FindAll(".transcript li").Select(li => li.TextContent).ShouldBe(["Book a table", "Let's find you <b>a table</b>."]);
            page.Find(".surfaces .a2ui-text").TextContent.ShouldBe("Book a table");
            page.Find(".status").TextContent.ShouldBe("Done.");
        });
        page.FindAll(".transcript b").ShouldBeEmpty();
        agent.Requests.ShouldHaveSingleItem().ShouldContain("\"text\":\"Book a table\"");
    }

    /// <summary>
    /// Proves that a refused request shows the client's error, in words the user can read.
    /// </summary>
    [Fact]
    public async Task Send_AgentRefuses_ShowsError()
    {
        // Arrange
        FakeAgentHandler agent = new(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"title":"Too many conversations."}""", Encoding.UTF8, "application/problem+json"),
        });
        await using BunitContext context = Context(agent);
        IRenderedComponent<Chat> page = context.Render<Chat>();

        // Act
        page.Find("input[aria-label=Message]").Change("Hello");
        page.Find("form").Submit();

        // Assert
        page.WaitForAssertion(() => page.Find("[role=alert]").TextContent.ShouldBe("The agent refused the request (503): Too many conversations."));
        page.Find(".status").TextContent.ShouldBe("The turn failed.");
    }

    /// <summary>
    /// Proves that a turn a surface's action starts gets its own transcript entry instead of adding to the previous
    /// answer.
    /// </summary>
    [Fact]
    public async Task Action_SecondTurn_StartsNewAgentEntry()
    {
        // Arrange
        int turns = 0;
        FakeAgentHandler agent = new(() => Interlocked.Increment(ref turns) == 1
            ? FakeAgentHandler.EventStream("Welcome.", WelcomeSurface)
            : FakeAgentHandler.EventStream("Let's find you a table."));
        await using BunitContext context = Context(agent);
        IRenderedComponent<Chat> page = context.Render<Chat>();
        page.Find("input[aria-label=Message]").Change("Hello");
        page.Find("form").Submit();
        page.WaitForAssertion(() => page.Find(".status").TextContent.ShouldBe("Done."));

        // Act
        page.Find(".surfaces button").Click();

        // Assert
        page.WaitForAssertion(() =>
        {
            page.FindAll(".transcript li").Select(li => li.TextContent).ShouldBe(["Hello", "Welcome.", "Let's find you a table."]);
            page.Find(".status").TextContent.ShouldBe("Done.");
        });
        agent.Requests.Count.ShouldBe(2);
    }

    /// <summary>
    /// Proves that text over the limit is refused on the server, because the input's maxlength is only a hint to the
    /// browser.
    /// </summary>
    [Fact]
    public async Task Send_TextOverLimit_RefusesIt()
    {
        // Arrange
        FakeAgentHandler agent = new(() => FakeAgentHandler.EventStream("Hi"));
        await using BunitContext context = Context(agent);
        IRenderedComponent<Chat> page = context.Render<Chat>();

        // Act
        page.Find("input[aria-label=Message]").Change(new string('a', 2001));
        page.Find("form").Submit();

        // Assert
        page.Find("[role=alert]").TextContent.ShouldBe("The message is longer than 2,000 characters.");
        agent.Requests.ShouldBeEmpty();
        page.FindAll(".transcript li").ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that blank text is not sent.
    /// </summary>
    [Fact]
    public async Task Send_BlankText_SendsNothing()
    {
        // Arrange
        FakeAgentHandler agent = new(() => FakeAgentHandler.EventStream("Hi"));
        await using BunitContext context = Context(agent);
        IRenderedComponent<Chat> page = context.Render<Chat>();

        // Act
        page.Find("input[aria-label=Message]").Change("   ");
        page.Find("form").Submit();

        // Assert
        agent.Requests.ShouldBeEmpty();
        page.FindAll(".transcript li").ShouldBeEmpty();
    }

    /// <summary>
    /// Builds a bUnit context with the sample's renderer services, whose agent client talks to the agent in memory.
    /// </summary>
    /// <returns>The context.</returns>
    /// <param name="agent">The agent.</param>
    private static BunitContext Context(FakeAgentHandler agent)
    {
        BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSampleRenderer(new SampleRendererOptions { AgentEndpoint = new Uri("https://agent.test/a2ui") });
        context.Services.Configure<HttpClientFactoryOptions>(SampleRenderer.HttpClientName, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = agent));
        return context;
    }
}
