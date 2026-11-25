namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Video</c> component. Plays a video. The renderer loads it only through the host's media allowlist.
/// </summary>
public sealed class Video : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Video";

    /// <summary>
    /// Gets or sets the video URL (<c>url</c>).
    /// </summary>
    public required A2UIValue Url { get; init; }

    /// <summary>
    /// Gets or sets the URL of the image shown before playback (<c>posterUrl</c>).
    /// </summary>
    public A2UIValue? PosterUrl { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("url", Url);
        writer.Set("posterUrl", PosterUrl);
    }
}
