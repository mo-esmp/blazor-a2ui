using System.Text.Json.Nodes;

namespace BlazorA2UI.Catalogs;

/// <summary>
/// How a component property references other components.
/// </summary>
public enum ChildReferenceKind
{
    /// <summary>
    /// A single component id (<c>Child</c> or <c>ComponentId</c>).
    /// </summary>
    Single,

    /// <summary>
    /// A static id list or a data-bound template (<c>ChildList</c>).
    /// </summary>
    List,

    /// <summary>
    /// An array of objects that each hold a <c>child</c> id (for example <c>Tabs.tabs</c>).
    /// </summary>
    NestedSingle,
}

/// <summary>
/// A catalog component type, read from its JSON Schema.
/// </summary>
public sealed class ComponentDefinition
{
    /// <summary>
    /// Creates a definition for the component type <paramref name="name"/>.
    /// </summary>
    public ComponentDefinition(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the component type name, as messages use it in <c>component</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Property names the schema declares, excluding <c>component</c>.
    /// </summary>
    public IReadOnlySet<string> Properties { get; init; } = new HashSet<string>();

    /// <summary>
    /// Gets the properties a component of this type must have.
    /// </summary>
    public IReadOnlySet<string> Required { get; init; } = new HashSet<string>();

    /// <summary>
    /// True when the component accepts <c>@checks</c>.
    /// </summary>
    public bool IsCheckable { get; init; }

    /// <summary>
    /// Properties that reference child components, and how.
    /// </summary>
    public IReadOnlyDictionary<string, ChildReferenceKind> ChildProperties { get; init; } =
        new Dictionary<string, ChildReferenceKind>();

    /// <summary>
    /// Allowed parent component types; null allows any parent.
    /// </summary>
    public IReadOnlyList<string>? AllowedParents { get; init; }

    /// <summary>
    /// Allowed child component types; null allows any child.
    /// </summary>
    public IReadOnlyList<string>? AllowedChildren { get; init; }

    /// <summary>
    /// The schema of each declared property (from the component's inline property blocks).
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> PropertySchemas { get; init; } = new Dictionary<string, JsonNode?>();

    /// <summary>
    /// For <see cref="ChildReferenceKind.NestedSingle"/> properties, the item key holding the child id.
    /// </summary>
    public IReadOnlyDictionary<string, string> NestedChildKeys { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// True for the stand-in definition of a component type no catalog declares.
    /// </summary>
    public bool IsUnknown { get; init; }

    /// <summary>
    /// Gets the component's full schema from the catalog, or null for components defined in code.
    /// </summary>
    public JsonObject? Schema { get; init; }

    /// <summary>
    /// A stand-in for a type the catalog does not declare; renderers show a placeholder for it.
    /// </summary>
    public static ComponentDefinition Unknown(string name) => new(name) { IsUnknown = true };

    /// <summary>
    /// Static catalog metadata (<c>metadata.extensions</c>) for this component type.
    /// </summary>
    public JsonObject? Metadata { get; init; }
}
