namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>Image</c> renderer and its media allowlist.
/// </summary>
public sealed class A2UIImageTests
{
    /// <summary>
    /// Proves that with no allowlist an image renders as a blocked placeholder, with no element that can fetch.
    /// </summary>
    [Fact]
    public void Render_NoAllowlist_RendersBlockedPlaceholder()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Image","url":"https://cdn.example.com/cat.png","description":"A cat"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.FindAll("img").ShouldBeEmpty();
        surface.FindAll("[src]").ShouldBeEmpty();
        surface.Markup.ShouldNotContain("cdn.example.com");
        surface.Find(".a2ui-media-blocked").GetAttribute("aria-label").ShouldBe("A cat");
    }

    /// <summary>
    /// Proves that an image under an allowed prefix renders with its URL and without a referrer.
    /// </summary>
    [Fact]
    public void Render_AllowedUrl_RendersImage()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Media.Allow("https://cdn.example.com/"));
        host.Load("""[{"id":"root","component":"Image","url":"https://cdn.example.com/cat.png","fit":"cover"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        AngleSharp.Dom.IElement image = surface.Find("img");
        image.GetAttribute("src").ShouldBe("https://cdn.example.com/cat.png");
        image.GetAttribute("referrerpolicy").ShouldBe("no-referrer");
        image.ClassList.ShouldContain("a2ui-fit-cover");
    }

    /// <summary>
    /// Proves that a bound URL that changes to one outside the allowlist switches the image back to the placeholder.
    /// </summary>
    [Fact]
    public void Render_BoundUrlLeavesAllowlist_RendersBlockedPlaceholder()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Media.Allow("https://cdn.example.com/"));
        host.Load("""[{"id":"root","component":"Image","url":{"@path":"/pic"}}]""", """{"pic":"https://cdn.example.com/cat.png"}""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.FindAll("img").ShouldHaveSingleItem();

        // Act
        host.Processor.ProcessOrThrow(JsonNode.Parse("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/pic","value":"https://evil.test/track.png"}}"""));

        // Assert
        surface.WaitForAssertion(() =>
        {
            surface.FindAll("img").ShouldBeEmpty();
            surface.Markup.ShouldNotContain("evil.test");
        });
    }

    /// <summary>
    /// Proves that an image without <c>variant</c> or <c>fit</c> renders the catalog defaults, <c>mediumFeature</c> and <c>fill</c>.
    /// </summary>
    [Fact]
    public void Render_NoVariantOrFit_RendersCatalogDefaults()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Media.Allow("https://cdn.example.com/"));
        host.Load("""[{"id":"root","component":"Image","url":"https://cdn.example.com/cat.png"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        AngleSharp.Dom.IElement image = surface.Find("img");
        image.ClassList.ShouldContain("a2ui-image-medium-feature");
        image.ClassList.ShouldContain("a2ui-fit-fill");
    }
}
