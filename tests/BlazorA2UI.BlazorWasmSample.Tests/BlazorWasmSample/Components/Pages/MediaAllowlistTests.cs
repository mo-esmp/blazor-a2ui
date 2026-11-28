using BlazorA2UI.BlazorWasmSample.Components.Pages;

namespace BlazorA2UI.Tests.BlazorWasmSample.Components.Pages;

/// <summary>
/// Tests for the <see cref="MediaAllowlist"/> page.
/// </summary>
public sealed class MediaAllowlistTests
{
    /// <summary>
    /// Proves that the image from an allowed prefix loads and the other renders as a blocked placeholder, so the
    /// browser never gets its URL.
    /// </summary>
    [Fact]
    public async Task Render_Allowlist_LoadsOneImageAndBlocksTheOther()
    {
        // Arrange
        await using BunitContext context = new();
        context.Services.AddSampleRenderer(new SampleRendererOptions { MediaPrefixes = ["http://localhost/media/"] });

        // Act
        IRenderedComponent<MediaAllowlist> page = context.Render<MediaAllowlist>();

        // Assert
        page.FindAll("img").ShouldHaveSingleItem().GetAttribute("src").ShouldBe("http://localhost/media/allowed.svg");
        page.FindAll(".a2ui-media-blocked").ShouldHaveSingleItem();
        page.Markup.ShouldNotContain(MediaAllowlist.BlockedUrl);
    }
}
