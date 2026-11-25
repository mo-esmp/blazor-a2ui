namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>Icon</c> renderer.
/// </summary>
public sealed class A2UIIconTests
{
    /// <summary>
    /// Proves that a named icon renders as a fixed class hook.
    /// </summary>
    [Fact]
    public void Render_KnownName_RendersIconClass()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Icon","name":"home"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find(".a2ui-icon-home").ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that an <c>svgPath</c> made only of path data is drawn.
    /// </summary>
    [Fact]
    public void Render_ValidSvgPath_RendersPath()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Icon","name":{"svgPath":"M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z"}}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find("path").GetAttribute("d").ShouldBe("M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z");
    }

    /// <summary>
    /// Proves that an <c>svgPath</c> holding anything but path data, such as markup or a URL, is not rendered.
    /// </summary>
    [Fact]
    public void Render_InvalidSvgPath_RendersNothing()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Icon","name":{"svgPath":"M0 0\"/><image href=\"https://evil.test/x\"/><path d=\"M0 0"}}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.FindAll("svg").ShouldBeEmpty();
        surface.Markup.ShouldNotContain("evil.test");
    }

    /// <summary>
    /// Proves that an <c>svgPath</c> object supplied through a data binding is drawn, and still checked as path data.
    /// </summary>
    /// <param name="path">The path the data model holds.</param>
    /// <param name="drawn">Whether it is valid path data and is drawn.</param>
    [Theory]
    [InlineData("M0 0L8 8", true)]
    [InlineData("M0 0\"/><image href=\"https://evil.test/x\"/>", false)]
    public void Render_BoundSvgPath_RendersOnlyValidPath(string path, bool drawn)
    {
        // Arrange
        using A2UITestHost host = new();
        JsonObject data = new() { ["icon"] = new JsonObject { ["svgPath"] = path } };
        host.Load("""[{"id":"root","component":"Icon","name":{"@path":"/icon"}}]""", data.ToJsonString());

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.FindAll("path").Count.ShouldBe(drawn ? 1 : 0);
        surface.Markup.ShouldNotContain("evil.test");
    }

    /// <summary>
    /// Proves that a bound icon name renders its class.
    /// </summary>
    [Fact]
    public void Render_BoundName_RendersIconClass()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"Icon","name":{"@path":"/icon"}}]""", """{"icon":"star"}""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find(".a2ui-icon-star").ShouldNotBeNull();
    }
}
