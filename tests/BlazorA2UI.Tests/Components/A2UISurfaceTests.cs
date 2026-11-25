using AngleSharp.Dom;

namespace BlazorA2UI.Tests.Components;

/// <summary>
/// Tests for <see cref="A2UISurface"/> and the rendering rules every component it hosts must keep.
/// </summary>
public sealed class A2UISurfaceTests
{
    /// <summary>
    /// Proves that a surface the agent has not created yet renders the placeholder, and renders the tree once it arrives.
    /// </summary>
    [Fact]
    public void Render_SurfaceCreatedLater_RendersTree()
    {
        // Arrange
        using A2UITestHost host = new();
        IRenderedComponent<A2UISurface> surface = host.Context.Render<A2UISurface>(parameters => parameters
            .Add(p => p.SurfaceId, A2UITestHost.SurfaceId)
            .Add(p => p.Placeholder, "Waiting"));
        surface.Markup.ShouldBe("Waiting");

        // Act
        host.Load("""[{"id":"root","component":"Text","text":"Hello"}]""");

        // Assert
        surface.WaitForAssertion(() => surface.Find(".a2ui-text").TextContent.ShouldBe("Hello"));
    }

    /// <summary>
    /// Proves that model text is HTML-encoded, so markup in a message is shown as text and never parsed.
    /// </summary>
    [Fact]
    public void Render_TextWithMarkup_RendersEscapedText()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Text","text":"<img src=x onerror=alert(1)><script>alert(1)</script>"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.FindAll("img").ShouldBeEmpty();
        surface.FindAll("script").ShouldBeEmpty();
        surface.Find(".a2ui-text").TextContent.ShouldBe("<img src=x onerror=alert(1)><script>alert(1)</script>");
    }

    /// <summary>
    /// Proves that a tree using every layout option renders no inline <c>style</c> attribute and only enumerated classes.
    /// </summary>
    [Fact]
    public void Render_EveryLayoutOption_RendersNoInlineStyle()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""
            [
              {"id":"root","component":"Column","children":["row","list","card","divider"],"justify":"spaceBetween","align":"center"},
              {"id":"row","component":"Row","children":["t1","img","btn"],"justify":"spaceEvenly","align":"stretch","weight":3},
              {"id":"t1","component":"Text","text":"caption","variant":"caption","weight":2},
              {"id":"img","component":"Image","url":"https://evil.test/x.png","variant":"avatar","fit":"cover"},
              {"id":"btn","component":"Button","child":"bt","variant":"primary","action":{"event":{"name":"go"}}},
              {"id":"bt","component":"Text","text":"Go"},
              {"id":"list","component":"List","children":["t2"],"direction":"horizontal","align":"end"},
              {"id":"t2","component":"Text","text":"item"},
              {"id":"card","component":"Card","child":"t3"},
              {"id":"t3","component":"Text","text":"in card"},
              {"id":"divider","component":"Divider","axis":"vertical"}
            ]
            """);

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        IElement[] all = [.. surface.FindAll("*")];
        all.ShouldNotBeEmpty();
        all.ShouldAllBe(e => !e.HasAttribute("style"));
        all.SelectMany(e => e.ClassList).ShouldAllBe(c => c.StartsWith("a2ui-", StringComparison.Ordinal));
        surface.Find(".a2ui-justify-space-between").ShouldNotBeNull();
        surface.Find(".a2ui-weight-3").ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that a layout value outside its enumeration selects no class and never reaches the markup.
    /// </summary>
    [Fact]
    public void Render_LayoutValueOutsideEnumeration_IsIgnored()
    {
        // Arrange
        using A2UITestHost host = new();
        string injected = "center\\\" style=\\\"background:url(https://evil.test/track)";
        host.Processor.Process(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{BasicCatalog.CatalogId}}}"}}"""));
        host.Processor.Process(JsonNode.Parse($$$"""{"version":"1.0","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Row","children":[],"justify":"{{{injected}}}"}]}}"""));

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find(".a2ui-row").ClassList.ShouldNotContain(c => c.StartsWith("a2ui-justify-", StringComparison.Ordinal));
        surface.Markup.ShouldNotContain("evil.test");
        surface.FindAll("[style]").ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that clicking a button sends its <c>event</c> to the agent and tells the page through <c>OnAction</c>.
    /// </summary>
    [Fact]
    public void Click_ButtonWithEvent_SendsActionAndNotifiesPage()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load(
            """
            [
              {"id":"root","component":"Button","child":"label","action":{"event":{"name":"submit","context":{"who":{"@path":"/name"}}}}},
              {"id":"label","component":"Text","text":"Send"}
            ]
            """,
            """{"name":"Ada"}""");
        List<A2UIActionEventArgs> notified = [];
        IRenderedComponent<A2UISurface> surface = host.Render(notified.Add);

        // Act
        surface.Find("button").Click();

        // Assert
        A2UIOutboundMessage sent = host.Sent.ShouldHaveSingleItem();
        sent.Kind.ShouldBe("action");
        JsonNode? action = sent.Message["action"];
        action?["name"]?.GetValue<string>().ShouldBe("submit");
        action?["context"]?["who"]?.GetValue<string>().ShouldBe("Ada");
        notified.ShouldHaveSingleItem().ComponentId.ShouldBe("root");
    }

    /// <summary>
    /// Proves that a component type with no registered renderer renders the placeholder instead of failing the page.
    /// </summary>
    [Fact]
    public void Render_ComponentWithoutRenderer_RendersPlaceholder()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Components.Unregister("Divider"));
        host.Load("""[{"id":"root","component":"Divider"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find(".a2ui-placeholder").ShouldNotBeNull();
        surface.FindAll("hr").ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that deleting the surface removes its tree from the page.
    /// </summary>
    [Fact]
    public void Process_DeleteSurface_ClearsTree()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Text","text":"Bye"}]""");
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Act
        host.Processor.ProcessOrThrow(JsonNode.Parse("""{"version":"1.0","deleteSurface":{"surfaceId":"s"}}"""));

        // Assert
        surface.WaitForAssertion(() => surface.FindAll(".a2ui-text").ShouldBeEmpty());
    }
}
