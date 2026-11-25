namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>ChoicePicker</c> renderer.
/// </summary>
public sealed class A2UIChoicePickerTests
{
    /// <summary>
    /// The picker's options, shared by the tests.
    /// </summary>
    private const string Options = """[{"label":"Red","value":"r"},{"label":"Blue","value":"b"}]""";

    /// <summary>
    /// Proves that a picker without <c>variant</c> follows the catalog default, <c>mutuallyExclusive</c>: radio buttons,
    /// and choosing one replaces the selection.
    /// </summary>
    [Fact]
    public void Change_NoVariant_SelectsOneValue()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load($$$"""[{"id":"root","component":"ChoicePicker","options":{{{Options}}},"value":{"@path":"/picked"}}]""", """{"picked":["r"]}""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.FindAll("input[type=radio]").Count.ShouldBe(2);

        // Act
        surface.FindAll("input")[1].Change("on");

        // Assert
        host.Processor.Surfaces[A2UITestHost.SurfaceId].DataModel.Get("/picked")?.ToJsonString().ShouldBe("""["b"]""");
    }

    /// <summary>
    /// Proves that <c>multipleSelection</c> renders checkboxes and adds to the selection.
    /// </summary>
    [Fact]
    public void Change_MultipleSelection_AddsValue()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load($$$"""[{"id":"root","component":"ChoicePicker","variant":"multipleSelection","options":{{{Options}}},"value":{"@path":"/picked"}}]""", """{"picked":["r"]}""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.FindAll("input[type=checkbox]").Count.ShouldBe(2);

        // Act
        surface.FindAll("input")[1].Change(true);

        // Assert
        host.Processor.Surfaces[A2UITestHost.SurfaceId].DataModel.Get("/picked")?.ToJsonString().ShouldBe("""["r","b"]""");
    }
}
