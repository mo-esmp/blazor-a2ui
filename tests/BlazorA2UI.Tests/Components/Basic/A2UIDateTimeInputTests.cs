namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>DateTimeInput</c> renderer.
/// </summary>
public sealed class A2UIDateTimeInputTests
{
    /// <summary>
    /// Proves that the input type follows <c>enableDate</c> and <c>enableTime</c>, whose catalog defaults are both
    /// false; with neither enabled the renderer falls back to a date input.
    /// </summary>
    /// <param name="flags">The flags as JSON properties, with a trailing comma, or empty for none.</param>
    /// <param name="expectedType">The input type the flags select.</param>
    [Theory]
    [InlineData("", "date")]
    [InlineData("\"enableDate\":true,", "date")]
    [InlineData("\"enableTime\":true,", "time")]
    [InlineData("\"enableDate\":true,\"enableTime\":true,", "datetime-local")]
    public void Render_Flags_SelectInputType(string flags, string expectedType)
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load($$$"""[{"id":"root","component":"DateTimeInput",{{{flags}}}"value":{"@path":"/when"}}]""", """{"when":""}""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.Find("input").GetAttribute("type").ShouldBe(expectedType);
    }
}
