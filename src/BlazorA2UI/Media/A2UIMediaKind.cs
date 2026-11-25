namespace BlazorA2UI.Media;

/// <summary>
/// What a media URL is for, so a host can allow images but not video, for example.
/// </summary>
public enum A2UIMediaKind
{
    /// <summary>
    /// The <c>url</c> of an <c>Image</c>.
    /// </summary>
    Image,

    /// <summary>
    /// The <c>url</c> of a <c>Video</c>.
    /// </summary>
    Video,

    /// <summary>
    /// The <c>posterUrl</c> of a <c>Video</c>.
    /// </summary>
    VideoPoster,

    /// <summary>
    /// The <c>url</c> of an <c>AudioPlayer</c>.
    /// </summary>
    Audio,
}
