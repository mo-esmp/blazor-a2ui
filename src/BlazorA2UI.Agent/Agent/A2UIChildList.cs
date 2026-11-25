using System.Collections;

namespace BlazorA2UI.Agent;

/// <summary>
/// The children of a container: either a fixed list of components, written with a collection expression
/// (<c>Children = [title, body]</c>), or a template repeated for each item of a data model list (<see cref="Template"/>).
/// </summary>
public sealed class A2UIChildList : IEnumerable<A2UIComponent>
{
    /// <summary>
    /// The fixed children, in order.
    /// </summary>
    private readonly List<A2UIComponent> _children = [];

    /// <summary>
    /// Gets the template component, or null for a fixed list.
    /// </summary>
    public A2UIComponent? TemplateComponent { get; private init; }

    /// <summary>
    /// Gets the data model path of the list the template repeats over, or null for a fixed list.
    /// </summary>
    public string? TemplatePath { get; private init; }

    /// <summary>
    /// Gets the fixed children.
    /// </summary>
    public IReadOnlyList<A2UIComponent> Children => _children;

    /// <summary>
    /// Creates a list that repeats a template for each item of a data model list. Inside the template, relative
    /// bindings such as <c>A2UIValue.Path("name")</c> read the current item.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="component"/> or <paramref name="path"/> is null.</exception>
    /// <param name="component">The template.</param>
    /// <param name="path">The path to the list in the data model.</param>
    public static A2UIChildList Template(A2UIComponent component, string path)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(path);
        return new A2UIChildList { TemplateComponent = component, TemplatePath = path };
    }

    /// <summary>
    /// Adds a fixed child.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The list is a template.</exception>
    /// <param name="child">The child.</param>
    public void Add(A2UIComponent child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (TemplateComponent is not null)
        {
            throw new InvalidOperationException("A template list has no fixed children.");
        }

        _children.Add(child);
    }

    /// <inheritdoc />
    public IEnumerator<A2UIComponent> GetEnumerator() => _children.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
