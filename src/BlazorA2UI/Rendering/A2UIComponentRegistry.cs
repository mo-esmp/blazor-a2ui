using System.Diagnostics.CodeAnalysis;
using BlazorA2UI.Components;
using BlazorA2UI.Components.Basic;

namespace BlazorA2UI.Rendering;

/// <summary>
/// Maps A2UI component types to the Razor components that render them. The basic catalog is registered by
/// default; a host adds its own catalog's components with <see cref="Register"/>.
/// </summary>
public sealed class A2UIComponentRegistry
{
    /// <summary>
    /// The renderers registered for one catalog, by catalog id and then component type.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, Type>> _byCatalog = new(StringComparer.Ordinal);

    /// <summary>
    /// The renderers that apply to every catalog, by component type.
    /// </summary>
    private readonly Dictionary<string, Type> _any = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a registry with the basic catalog's components.
    /// </summary>
    public A2UIComponentRegistry()
    {
        Register<A2UIText>("Text");
        Register<A2UIImage>("Image");
        Register<A2UIIcon>("Icon");
        Register<A2UIVideo>("Video");
        Register<A2UIAudioPlayer>("AudioPlayer");
        Register<A2UIRow>("Row");
        Register<A2UIColumn>("Column");
        Register<A2UIList>("List");
        Register<A2UICard>("Card");
        Register<A2UITabs>("Tabs");
        Register<A2UIModal>("Modal");
        Register<A2UIDivider>("Divider");
        Register<A2UIButton>("Button");
        Register<A2UITextField>("TextField");
        Register<A2UICheckBox>("CheckBox");
        Register<A2UIChoicePicker>("ChoicePicker");
        Register<A2UISlider>("Slider");
        Register<A2UIDateTimeInput>("DateTimeInput");
    }

    /// <summary>
    /// Registers <typeparamref name="TComponent"/> as the renderer of <paramref name="componentType"/>, replacing an earlier one.
    /// </summary>
    /// <typeparam name="TComponent">The Razor component, derived from <see cref="A2UIComponentBase"/>.</typeparam>
    /// <param name="componentType">The A2UI component type, as messages name it.</param>
    /// <param name="catalogId">The catalog it applies to, or null for every catalog. A catalog-specific renderer wins.</param>
    /// <returns>This registry, for chaining.</returns>
    public A2UIComponentRegistry Register<TComponent>(string componentType, string? catalogId = null)
        where TComponent : A2UIComponentBase
    {
        ArgumentException.ThrowIfNullOrEmpty(componentType);
        Dictionary<string, Type> target = _any;
        if (catalogId is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(catalogId);
            if (!_byCatalog.TryGetValue(catalogId, out Dictionary<string, Type>? map))
            {
                map = new Dictionary<string, Type>(StringComparer.Ordinal);
                _byCatalog[catalogId] = map;
            }

            target = map;
        }

        target[componentType] = typeof(TComponent);
        return this;
    }

    /// <summary>
    /// Removes the renderer of <paramref name="componentType"/>, for one catalog or for every catalog, so the host
    /// can turn a component off. A component without a renderer draws a placeholder.
    /// </summary>
    /// <param name="componentType">The component type, such as <c>Video</c>.</param>
    /// <param name="catalogId">The catalog the renderer was registered for, or null for the renderer that applies to every catalog.</param>
    /// <returns>False when no such renderer was registered; the registry is then unchanged.</returns>
    public bool Unregister(string componentType, string? catalogId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentType);
        if (catalogId is null)
        {
            return _any.Remove(componentType);
        }

        return _byCatalog.TryGetValue(catalogId, out Dictionary<string, Type>? map) && map.Remove(componentType);
    }

    /// <summary>
    /// Finds the renderer for a component.
    /// </summary>
    /// <param name="component">The component to render.</param>
    /// <param name="renderer">The Razor component type, when one is registered.</param>
    /// <returns>False when no renderer is registered; the component then renders as a placeholder.</returns>
    public bool TryGetRenderer(ComponentInstance component, [MaybeNullWhen(false)] out Type renderer)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_byCatalog.TryGetValue(component.Catalog.CatalogId, out Dictionary<string, Type>? map)
            && map.TryGetValue(component.Type, out renderer))
        {
            return true;
        }

        return _any.TryGetValue(component.Type, out renderer);
    }
}
