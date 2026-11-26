namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>AudioPlayer</c> component. Plays audio. The renderer loads it only through the host's media allowlist.
/// </summary>
public sealed class AudioPlayer : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "AudioPlayer";

    /// <summary>
    /// Gets or sets the audio URL (<c>url</c>).
    /// </summary>
    public required A2UIValue Url { get; init; }

    /// <summary>
    /// Gets or sets a description of the audio (<c>description</c>).
    /// </summary>
    public A2UIValue? Description { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("url", Url);
        writer.Set("description", Description);
    }
}
