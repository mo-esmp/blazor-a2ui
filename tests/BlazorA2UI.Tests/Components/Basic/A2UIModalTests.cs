namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>Modal</c> renderer's keyboard behavior.
/// </summary>
public sealed class A2UIModalTests
{
    /// <summary>
    /// The modal under test: a button opens a dialog holding one text.
    /// </summary>
    private const string Tree = """
        [
          {"id":"root","component":"Modal","trigger":"open","content":"body"},
          {"id":"open","component":"Button","child":"openLabel","action":{"event":{"name":"opened"}}},
          {"id":"openLabel","component":"Text","text":"Open"},
          {"id":"body","component":"Text","text":"Inside"}
        ]
        """;

    /// <summary>
    /// Proves that opening the modal moves focus into the dialog and surrounds it with focus guards that keep
    /// Tab inside.
    /// </summary>
    [Fact]
    public void Click_Trigger_FocusesDialogBetweenGuards()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load(Tree);
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Act
        surface.Find(".a2ui-modal-trigger").Click();

        // Assert
        surface.Find("[role=dialog]").GetAttribute("tabindex").ShouldBe("-1");
        surface.FindAll(".a2ui-focus-guard").Count.ShouldBe(2);
        host.Context.JSInterop.VerifyFocusAsyncInvoke();
    }

    /// <summary>
    /// Proves that Escape closes the modal and focus returns to the trigger.
    /// </summary>
    [Fact]
    public void KeyDown_Escape_ClosesAndRestoresFocus()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load(Tree);
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.Find(".a2ui-modal-trigger").Click();

        // Act
        surface.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        // Assert
        surface.FindAll("[role=dialog]").ShouldBeEmpty();
        host.Context.JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2);
    }

    /// <summary>
    /// Proves that focus reaching a guard is sent back into the dialog.
    /// </summary>
    [Fact]
    public void Focus_Guard_RefocusesDialog()
    {
        // Arrange
        using A2UITestHost host = new();
        host.Load(Tree);
        IRenderedComponent<A2UISurface> surface = host.Render();
        surface.Find(".a2ui-modal-trigger").Click();

        // Act
        surface.FindAll(".a2ui-focus-guard")[1].Focus();

        // Assert
        host.Context.JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2);
        surface.Find("[role=dialog]").ShouldNotBeNull();
    }
}
