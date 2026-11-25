using System.Diagnostics.CodeAnalysis;

namespace BlazorA2UI.Catalogs;

/// <summary>
/// The catalogs a renderer supports, by id. All must target the same protocol version.
/// </summary>
public sealed class CatalogRegistry
{
    /// <summary>
    /// The registered catalogs, by catalog id.
    /// </summary>
    private readonly Dictionary<string, Catalog> _catalogs = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a registry holding <paramref name="catalogs"/>.
    /// </summary>
    /// <exception cref="A2UICatalogException">Two catalogs share an id.</exception>
    public CatalogRegistry(IEnumerable<Catalog>? catalogs = null)
    {
        foreach (Catalog catalog in catalogs ?? [])
        {
            Add(catalog);
        }
    }

    /// <summary>
    /// Gets every registered catalog.
    /// </summary>
    public IReadOnlyCollection<Catalog> Catalogs => _catalogs.Values;

    /// <summary>
    /// Registers a catalog.
    /// </summary>
    /// <exception cref="A2UICatalogException">A catalog with the same id is already registered; the registry is unchanged.</exception>
    public void Add(Catalog catalog)
    {
        if (_catalogs.ContainsKey(catalog.CatalogId))
        {
            throw new A2UICatalogException($"A catalog with id '{catalog.CatalogId}' is already registered.");
        }

        _catalogs[catalog.CatalogId] = catalog;
    }

    /// <summary>
    /// Looks up a catalog by id.
    /// </summary>
    /// <returns>False when no catalog has that id; <paramref name="catalog"/> is then null.</returns>
    public bool TryGet(string catalogId, [MaybeNullWhen(false)] out Catalog catalog) => _catalogs.TryGetValue(catalogId, out catalog);

    /// <summary>
    /// Returns the catalog, or throws a catalog error naming it.
    /// </summary>
    public Catalog Get(string catalogId) => TryGet(catalogId, out Catalog? catalog)
        ? catalog
        : throw new A2UICatalogException($"Catalog '{catalogId}' is not available on this renderer.");
}
