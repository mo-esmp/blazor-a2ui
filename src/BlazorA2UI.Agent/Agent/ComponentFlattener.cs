namespace BlazorA2UI.Agent;

/// <summary>
/// Turns component trees into the wire format: a flat list where parents reference children by id.
/// </summary>
/// <remarks>
/// Ids are assigned in two passes, so an id the agent chose is never handed out to another component. The first pass
/// collects every component and reserves the chosen ids. The second gives each remaining component an id made from
/// the nearest ancestor that has a chosen id, its type and a counter: <c>card__text_1</c>. A later update of
/// <c>card</c> therefore generates the same ids again and replaces those components, and leaves the rest of the
/// surface alone. Children are written before their parents, so every reference points to a component that came
/// earlier in the list.
/// </remarks>
internal sealed class ComponentFlattener
{
    /// <summary>
    /// The deepest tree accepted, counted from a top-level component. It matches the renderer's default
    /// <see cref="Processing.A2UIProcessorOptions.MaxTreeDepth"/>.
    /// </summary>
    internal const int MaxDepth = 50;

    /// <summary>
    /// The most components one flatten writes. It matches the renderer's default
    /// <see cref="Processing.A2UIProcessorOptions.MaxComponentsPerSurface"/>.
    /// </summary>
    internal const int MaxComponents = 5_000;

    /// <summary>
    /// Every collected component, in the order it was first reached.
    /// </summary>
    private readonly List<A2UIComponent> _preOrder = [];

    /// <summary>
    /// Every collected component, children before parents: the output order.
    /// </summary>
    private readonly List<A2UIComponent> _postOrder = [];

    /// <summary>
    /// Every collected component.
    /// </summary>
    private readonly HashSet<A2UIComponent> _collected = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The id prefix each component without a chosen id inherits: the nearest chosen id above it.
    /// </summary>
    private readonly Dictionary<A2UIComponent, string?> _prefixes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The components on the current collection path, to find cycles.
    /// </summary>
    private readonly HashSet<A2UIComponent> _visiting = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The id of every collected component, once assigned.
    /// </summary>
    private readonly Dictionary<A2UIComponent, string> _ids = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The component that owns each id.
    /// </summary>
    private readonly Dictionary<string, A2UIComponent> _owners = new(StringComparer.Ordinal);

    /// <summary>
    /// The ids that references in the trees point to.
    /// </summary>
    private readonly List<string> _references = [];

    /// <summary>
    /// The next counter for each generated id stem.
    /// </summary>
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a flattener for one message.
    /// </summary>
    private ComponentFlattener()
    {
    }

    /// <summary>
    /// Flattens component trees into one wire component list.
    /// </summary>
    /// <exception cref="ArgumentException">A top-level component is a reference, has no id where one is required, or has the wrong id.</exception>
    /// <exception cref="InvalidOperationException">
    /// Two components share an id, a component contains itself, or the tree is deeper or larger than the renderer accepts.
    /// </exception>
    /// <param name="tops">The top-level components.</param>
    /// <param name="rootId">The id the single top-level component must have (<c>root</c>), or null when each top-level component must name its own id.</param>
    public static JsonArray Flatten(IReadOnlyList<A2UIComponent> tops, string? rootId)
    {
        ComponentFlattener tree = new();
        foreach (A2UIComponent top in tops)
        {
            ArgumentNullException.ThrowIfNull(top, nameof(tops));
            if (top is A2UIComponentRef)
            {
                throw new ArgumentException("A top-level component cannot be a reference.", nameof(tops));
            }

            string? id = top.Id;
            if (rootId is not null)
            {
                if (id is not null && id != rootId)
                {
                    throw new ArgumentException($"The root component's id must be '{rootId}' or unset, not '{id}'.", nameof(tops));
                }

                id = rootId;
            }
            else if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("Each top-level component of a partial update needs an id, so the update can replace it.", nameof(tops));
            }

            tree.Reserve(top, id);
            tree.Collect(top, null, 0);
        }

        tree.AssignIds();
        tree.CheckReferences();
        JsonArray output = [];
        foreach (A2UIComponent component in tree._postOrder)
        {
            output.Add(tree.Write(component));
        }

        return output;
    }

    /// <summary>
    /// Returns the id of a collected component, or the id a reference points to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The component was not collected: it changed between the two passes.</exception>
    /// <param name="component">The component.</param>
    internal string IdOf(A2UIComponent component)
    {
        if (component is A2UIComponentRef reference)
        {
            // A reference always has the id it was created with.
            return reference.Id!;
        }

        return _ids.TryGetValue(component, out string? id)
            ? id
            : throw new InvalidOperationException($"A {component.ComponentName} child was added while the tree was being written.");
    }

    /// <summary>
    /// Collects a component and, depth first, its children.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tree has a cycle, is too deep, is too large, or repeats an id.</exception>
    /// <param name="component">The component.</param>
    /// <param name="prefix">The nearest chosen id above it.</param>
    /// <param name="depth">Its depth below the top-level component.</param>
    private void Collect(A2UIComponent component, string? prefix, int depth)
    {
        if (component is A2UIComponentRef reference)
        {
            // A reference always has the id it was created with.
            _references.Add(reference.Id!);
            return;
        }

        if (_visiting.Contains(component))
        {
            throw new InvalidOperationException($"A {component.ComponentName} component contains itself.");
        }

        if (_collected.Contains(component))
        {
            return; // A component used twice is written once, and both parents reference it.
        }

        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"The component tree is deeper than {MaxDepth} levels.");
        }

        if (_collected.Count >= MaxComponents)
        {
            throw new InvalidOperationException($"The component tree has more than {MaxComponents} components.");
        }

        if (string.IsNullOrEmpty(component.ComponentName))
        {
            throw new InvalidOperationException($"{component.GetType().Name} has no component name.");
        }

        if (component.Id is { Length: > 0 } chosen)
        {
            Reserve(component, chosen);
        }

        string? ownId = _ids.GetValueOrDefault(component);
        A2UIComponentWriter collector = new(this, null);
        component.WriteProperties(collector);
        _collected.Add(component);
        _prefixes[component] = prefix;
        _preOrder.Add(component);
        _visiting.Add(component);
        foreach (A2UIComponent child in collector.Found)
        {
            Collect(child, ownId ?? prefix, depth + 1);
        }

        _visiting.Remove(component);
        _postOrder.Add(component);
    }

    /// <summary>
    /// Gives a component a chosen id.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another component already has the id.</exception>
    /// <param name="component">The component.</param>
    /// <param name="id">The id.</param>
    private void Reserve(A2UIComponent component, string id)
    {
        if (_owners.TryGetValue(id, out A2UIComponent? owner))
        {
            if (!ReferenceEquals(owner, component))
            {
                throw new InvalidOperationException($"Two components have the id '{id}'.");
            }

            return;
        }

        _owners[id] = component;
        _ids[component] = id;
    }

    /// <summary>
    /// Rejects a reference to a component this flatten writes. Such a reference can close a cycle (a root that
    /// references <c>root</c>), and the component object itself can be placed instead. References to components already
    /// on the surface are left for the renderer or <see cref="A2UIMessageValidator"/> to check.
    /// </summary>
    /// <exception cref="InvalidOperationException">A reference points to a component in the trees being flattened.</exception>
    private void CheckReferences()
    {
        foreach (string id in _references)
        {
            if (_owners.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"A reference points to '{id}', which this message writes. Place the component itself instead of a reference.");
            }
        }
    }

    /// <summary>
    /// Gives every component without a chosen id a generated one, in collection order.
    /// </summary>
    private void AssignIds()
    {
        foreach (A2UIComponent component in _preOrder)
        {
            if (_ids.ContainsKey(component))
            {
                continue;
            }

            string type = A2UIEnum.LowerFirst(component.ComponentName);
            string stem = _prefixes[component] is { } prefix ? $"{prefix}__{type}_" : type + "_";
            int next = _counters.GetValueOrDefault(stem, 1);
            string id;
            do
            {
                id = stem + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                next++;
            }
            while (_owners.ContainsKey(id));

            _counters[stem] = next;
            _owners[id] = component;
            _ids[component] = id;
        }
    }

    /// <summary>
    /// Writes one component in wire form.
    /// </summary>
    /// <param name="component">The component.</param>
    private JsonObject Write(A2UIComponent component)
    {
        JsonObject json = new()
        {
            ["id"] = _ids[component],
            ["component"] = component.ComponentName,
        };
        if (component.CatalogId is not null)
        {
            json["@catalogId"] = component.CatalogId;
        }

        if (component.Accessibility is not null)
        {
            json["@accessibility"] = component.Accessibility.ToJson();
        }

        if (component.Metadata is not null)
        {
            json["@metadata"] = component.Metadata.DeepClone();
        }

        component.WriteProperties(new A2UIComponentWriter(this, json));
        return json;
    }
}
