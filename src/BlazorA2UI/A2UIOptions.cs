using BlazorA2UI.Catalogs;
using BlazorA2UI.Media;
using BlazorA2UI.Rendering;

namespace BlazorA2UI;

/// <summary>
/// Settings for <see cref="A2UIServiceCollectionExtensions.AddA2UI"/>.
/// </summary>
public sealed class A2UIOptions
{
    /// <summary>
    /// Gets the media allowlist. It starts empty, which blocks every image, video and audio URL.
    /// </summary>
    public A2UIMediaOptions Media { get; } = new();

    /// <summary>
    /// Gets the processor settings: limits, timeouts and validation.
    /// </summary>
    public A2UIProcessorOptions Processor { get; } = new();

    /// <summary>
    /// Gets the catalogs to support besides the basic catalog.
    /// </summary>
    public IList<Catalog> Catalogs { get; } = [];

    /// <summary>
    /// Gets the registry of Razor components that render each component type.
    /// </summary>
    public A2UIComponentRegistry Components { get; } = new();
}
