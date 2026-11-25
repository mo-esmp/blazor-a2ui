namespace BlazorA2UI.Tests.Components.Basic;

/// <summary>
/// Tests for the <c>Video</c> renderer and its media allowlist.
/// </summary>
public sealed class A2UIVideoTests
{
    /// <summary>
    /// Proves that a video outside the allowlist renders a blocked placeholder.
    /// </summary>
    [Fact]
    public void Render_UrlNotAllowed_RendersBlockedPlaceholder()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Media.Allow("https://cdn.example.com/", A2UIMediaKind.Image));
        host.Load("""[{"id":"root","component":"Video","url":"https://cdn.example.com/clip.mp4"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        surface.FindAll("video").ShouldBeEmpty();
        surface.Find(".a2ui-media-blocked").ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that the poster is checked on its own: an allowed video with an unlisted poster renders without the
    /// poster, and the video never preloads.
    /// </summary>
    [Fact]
    public void Render_PosterNotAllowed_RendersVideoWithoutPoster()
    {
        // Arrange
        using A2UITestHost host = new(options => options.Media.Allow("https://cdn.example.com/", A2UIMediaKind.Video));
        host.Load("""[{"id":"root","component":"Video","url":"https://cdn.example.com/clip.mp4","posterUrl":"https://cdn.example.com/poster.png"}]""");

        // Act
        IRenderedComponent<A2UISurface> surface = host.Render();

        // Assert
        AngleSharp.Dom.IElement video = surface.Find("video");
        video.GetAttribute("src").ShouldBe("https://cdn.example.com/clip.mp4");
        video.HasAttribute("poster").ShouldBeFalse();
        video.GetAttribute("preload").ShouldBe("none");
    }
}
