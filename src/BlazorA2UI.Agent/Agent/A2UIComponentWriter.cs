namespace BlazorA2UI.Agent;

/// <summary>
/// Writes one component's properties in wire form. A component's <see cref="A2UIComponent.WriteProperties"/> receives
/// it; unset (null) values are skipped, so optional properties need no checks.
/// </summary>
public sealed class A2UIComponentWriter
{
    /// <summary>
    /// The tree being flattened; it resolves children to ids.
    /// </summary>
    private readonly ComponentFlattener _tree;

    /// <summary>
    /// The object being written, or null while the tree only collects children.
    /// </summary>
    private readonly JsonObject? _target;

    /// <summary>
    /// Creates a writer.
    /// </summary>
    /// <param name="tree">The tree being flattened.</param>
    /// <param name="target">The object to write, or null to only collect children.</param>
    internal A2UIComponentWriter(ComponentFlattener tree, JsonObject? target)
    {
        _tree = tree;
        _target = target;
    }

    /// <summary>
    /// The children found while collecting, in the order the component named them.
    /// </summary>
    internal List<A2UIComponent> Found { get; } = [];

    /// <summary>
    /// Writes a value that may be a literal, a binding or a call.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value, or null to leave the property out.</param>
    public void Set(string name, A2UIValue? value)
    {
        CheckName(name);
        if (value is not null && _target is not null)
        {
            _target[name] = value.ToJson();
        }
    }

    /// <summary>
    /// Writes a plain number.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved, or the number is not finite.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="value">The number, or null to leave the property out.</param>
    public void Set(string name, double? value)
    {
        CheckName(name);
        if (value is { } number)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException($"'{name}' must be a finite number.", nameof(value));
            }

            if (_target is not null)
            {
                _target[name] = number;
            }
        }
    }

    /// <summary>
    /// Writes a plain boolean.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="value">The boolean, or null to leave the property out.</param>
    public void Set(string name, bool? value)
    {
        CheckName(name);
        if (value is { } flag && _target is not null)
        {
            _target[name] = flag;
        }
    }

    /// <summary>
    /// Writes a JSON value as it is, for properties that take plain JSON rather than a dynamic value.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value, copied; null leaves the property out.</param>
    public void SetJson(string name, JsonNode? value)
    {
        CheckName(name);
        if (value is not null && _target is not null)
        {
            _target[name] = value.DeepClone();
        }
    }

    /// <summary>
    /// Writes an enumeration member by its wire name (<c>SpaceBetween</c> becomes <c>spaceBetween</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <param name="value">The member, or null to leave the property out.</param>
    public void SetEnum<T>(string name, T? value)
        where T : struct, Enum
    {
        CheckName(name);
        if (value is { } member)
        {
            string wire = A2UIEnum.ToWire(member);
            if (_target is not null)
            {
                _target[name] = wire;
            }
        }
    }

    /// <summary>
    /// Writes a single child slot as the child's id.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="child">The child, or null to leave the slot out.</param>
    public void SetChild(string name, A2UIComponent? child)
    {
        CheckName(name);
        if (child is not null)
        {
            string id = ChildId(child);
            if (_target is not null)
            {
                _target[name] = id;
            }
        }
    }

    /// <summary>
    /// Writes a child list: an array of ids, or a <c>{"componentId", "path"}</c> template.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="children">The children, or null to leave the property out.</param>
    public void SetChildren(string name, A2UIChildList? children)
    {
        CheckName(name);
        if (children is null)
        {
            return;
        }

        if (children.TemplateComponent is { } template)
        {
            string id = ChildId(template);
            if (_target is not null)
            {
                _target[name] = new JsonObject { ["componentId"] = id, ["path"] = children.TemplatePath };
            }

            return;
        }

        JsonArray ids = [];
        foreach (A2UIComponent child in children.Children)
        {
            ids.Add(ChildId(child));
        }

        if (_target is not null)
        {
            _target[name] = ids;
        }
    }

    /// <summary>
    /// Writes an action.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved.</exception>
    /// <param name="name">The property name.</param>
    /// <param name="action">The action, or null to leave the property out.</param>
    public void SetAction(string name, A2UIAction? action)
    {
        CheckName(name);
        if (action is not null && _target is not null)
        {
            _target[name] = action.ToJson();
        }
    }

    /// <summary>
    /// Writes the component's validation rules (<c>@checks</c>).
    /// </summary>
    /// <param name="checks">The rules, or null or empty to leave the property out.</param>
    public void SetChecks(IReadOnlyList<A2UICheck>? checks)
    {
        if (checks is not { Count: > 0 } || _target is null)
        {
            return;
        }

        JsonArray items = [];
        foreach (A2UICheck check in checks)
        {
            items.Add(check.ToJson());
        }

        _target["@checks"] = items;
    }

    /// <summary>
    /// Returns the id a child gets on the wire, for properties that nest children inside other objects (such as
    /// <c>Tabs</c>). The child is added to the flattened output.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <param name="child">The child.</param>
    public string ChildId(A2UIComponent child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_target is null)
        {
            Found.Add(child);
            return string.Empty;
        }

        return _tree.IdOf(child);
    }

    /// <summary>
    /// Rejects the names the builder writes itself and the reserved <c>@</c> names.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or reserved.</exception>
    /// <param name="name">The property name.</param>
    private static void CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name is "id" or "component" || A2UIValue.IsReservedKey(name))
        {
            throw new ArgumentException($"'{name}' is written by the builder and cannot be set as a property.", nameof(name));
        }
    }
}
