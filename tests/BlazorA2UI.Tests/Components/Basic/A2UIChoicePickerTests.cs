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

    /// <summary>
    /// Proves that checking a visible option keeps selected values the picker does not render, such as one past
    /// the <c>MaxOptions</c> cap, and their order.
    /// </summary>
    [Fact]
    public void Change_SelectionBeyondCap_IsKept()
    {
        // Arrange
        using A2UITestHost host = new();
        JsonArray options = [];
        for (int i = 0; i <= A2UIChoicePicker.MaxOptions; i++)
        {
            options.Add(new JsonObject { ["label"] = $"Option {i}", ["value"] = $"v{i}" });
        }

        string last = $"v{A2UIChoicePicker.MaxOptions}";
        host.Load(
            $$$"""[{"id":"root","component":"ChoicePicker","variant":"multipleSelection","options":{{{options.ToJsonString()}}},"value":{"@path":"/picked"}}]""",
            $$$"""{"picked":["{{{last}}}"]}""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.FindAll("input[type=checkbox]").Count.ShouldBe(A2UIChoicePicker.MaxOptions);

        // Act
        surface.FindAll("input")[0].Change(true);

        // Assert
        host.Processor.Surfaces[A2UITestHost.SurfaceId].DataModel.Get("/picked")?.ToJsonString().ShouldBe($$$"""["{{{last}}}","v0"]""");
    }

    /// <summary>
    /// Proves that turning <c>filterable</c> off drops a typed query, so options it hid are shown again.
    /// </summary>
    [Fact]
    public void Process_FilterableTurnedOff_ShowsAllOptions()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load($$$"""[{"id":"root","component":"ChoicePicker","filterable":true,"options":{{{Options}}},"value":["r"]}]""");
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.Find("input[type=search]").Input("Red");
        surface.FindAll(".a2ui-choice").Count.ShouldBe(1);

        // Act
        host.Processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"ChoicePicker","filterable":false,"options":{{{Options}}},"value":["r"]}]}}"""));

        // Assert
        surface.WaitForAssertion(() =>
        {
            surface.FindAll("input[type=search]").ShouldBeEmpty();
            surface.FindAll(".a2ui-choice").Count.ShouldBe(2);
        });
    }
}
