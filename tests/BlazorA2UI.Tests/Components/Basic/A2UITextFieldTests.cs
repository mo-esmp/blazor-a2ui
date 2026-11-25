namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>TextField</c> renderer's two-way binding.
/// </summary>
public sealed class A2UITextFieldTests
{
    /// <summary>
    /// Proves that the field shows its bound value and writes what the user types back to the data model.
    /// </summary>
    [Fact]
    public void Change_BoundField_WritesDataModel()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"TextField","label":"Name","value":{"@path":"/name"}}]""", """{"name":"Ada"}""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.Find("input").GetAttribute("value").ShouldBe("Ada");

        // Act
        surface.Find("input").Change("Grace");

        // Assert
        host.Processor.Surfaces[A2UITestHost.SurfaceId].DataModel.Get("/name")?.GetValue<string>().ShouldBe("Grace");
    }

    /// <summary>
    /// Proves that the <c>obscured</c> variant renders a password input.
    /// </summary>
    [Fact]
    public void Render_ObscuredVariant_RendersPasswordInput()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load("""[{"id":"root","component":"TextField","label":"PIN","variant":"obscured","value":{"@path":"/pin"}}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find("input").GetAttribute("type").ShouldBe("password");
    }
}
