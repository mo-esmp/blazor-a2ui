namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The basic catalog's <c>Image</c> component. Displays an image. The renderer loads it only through the host's media allowlist.
/// </summary>
public sealed class Image : BasicComponent
{
    /// <inheritdoc />
    public override string ComponentName => "Image";

    /// <summary>
    /// Gets or sets the image URL (<c>url</c>).
    /// </summary>
    public required A2UIValue Url { get; init; }

    /// <summary>
    /// Gets or sets the alternative text (<c>description</c>).
    /// </summary>
    public A2UIValue? Description { get; init; }

    /// <summary>
    /// Gets or sets how the image fills its box; the renderer defaults to <see cref="ImageFit.Fill"/> (<c>fit</c>).
    /// </summary>
    public ImageFit? Fit { get; init; }

    /// <summary>
    /// Gets or sets the size preset; the renderer defaults to <see cref="ImageVariant.MediumFeature"/> (<c>variant</c>).
    /// </summary>
    public ImageVariant? Variant { get; init; }

    /// <inheritdoc />
    private protected override void WriteBasicProperties(A2UIComponentWriter writer)
    {
        writer.Set("url", Url);
        writer.Set("description", Description);
        writer.SetEnum("fit", Fit);
        writer.SetEnum("variant", Variant);
    }
}
