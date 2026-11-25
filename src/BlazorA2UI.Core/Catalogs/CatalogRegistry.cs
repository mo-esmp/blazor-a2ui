namespace BlazorA2UI.Catalogs;

/// <summary>The catalogs a renderer supports, by id. All must target the same protocol version.</summary>
public sealed class CatalogRegistry
{
    private readonly Dictionary<string, Catalog> _catalogs = new(StringComparer.Ordinal);

    public CatalogRegistry(IEnumerable<Catalog>? catalogs = null)
    {
        foreach (Catalog catalog in catalogs ?? [])
        {
            Add(catalog);
        }
    }

    public IReadOnlyCollection<Catalog> Catalogs => _catalogs.Values;

    public void Add(Catalog catalog)
    {
        if (_catalogs.ContainsKey(catalog.CatalogId))
        {
            throw new A2UICatalogException($"A catalog with id '{catalog.CatalogId}' is already registered.");
        }

        _catalogs[catalog.CatalogId] = catalog;
    }

    public bool TryGet(string catalogId, out Catalog catalog) => _catalogs.TryGetValue(catalogId, out catalog!);

    /// <summary>Returns the catalog, or throws a catalog error naming it.</summary>
    public Catalog Get(string catalogId) => TryGet(catalogId, out Catalog? catalog)
        ? catalog
        : throw new A2UICatalogException($"Catalog '{catalogId}' is not available on this renderer.");
}
