using System.Text.Json.Nodes;

namespace BlazorA2UI.Data;

/// <summary>
/// The reactive data model of one surface: a JSON document addressed with JSON
/// Pointers. Writes create missing containers (a list when the next segment is a
/// numeric index, an object otherwise) and notify observers whose value changed.
/// </summary>
public sealed class DataModel
{
    /// <summary>Largest list index a write may address.</summary>
    public const int MaxListIndex = 10_000;

    private readonly List<Observer> _observers = [];
    private JsonNode? _root;

    public DataModel(JsonNode? initial = null)
    {
        _root = initial?.DeepClone() ?? new JsonObject();
    }

    /// <summary>Raised after every write that changed the model, with the canonical path written.</summary>
    public event Action<string>? Changed;

    /// <summary>The root value. Treat as read-only; write with <see cref="Set"/>.</summary>
    public JsonNode? Root => _root;

    /// <summary>Reads the value at <paramref name="path"/>.</summary>
    /// <returns>True when the path holds a value (which may be JSON null).</returns>
    public bool TryGet(string path, out JsonNode? value) => TryGet(JsonPointer.Parse(path), out value);

    /// <summary>Reads the value at <paramref name="path"/>, or null when absent.</summary>
    public JsonNode? Get(string path) => TryGet(path, out var value) ? value : null;

    internal bool TryGet(IReadOnlyList<string> segments, out JsonNode? value) => TryGet(_root, segments, out value);

    private static bool TryGet(JsonNode? start, IReadOnlyList<string> segments, out JsonNode? value)
    {
        var current = start;
        foreach (var segment in segments)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    current = child;
                    break;
                case JsonArray arr when TryParseIndex(segment, out var index) && index < arr.Count:
                    current = arr[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="path"/> (a defensive copy is stored).
    /// Passing JSON null stores null; use <see cref="Delete"/> to remove a key.
    /// </summary>
    public void Set(string path, JsonNode? value)
    {
        var segments = JsonPointer.Parse(path);
        var copy = value?.DeepClone();
        TryGet(segments, out var oldValue);
        var existed = TryGet(segments, out _);

        if (segments.Count == 0)
        {
            _root = copy;
            AfterWrite(segments, existed, oldValue, true, copy);
            return;
        }

        var parent = Vivify(segments);
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = copy;
                break;
            case JsonArray arr:
                var index = ParseWriteIndex(last, path);
                while (arr.Count <= index)
                {
                    arr.Add(null);
                }

                arr[index] = copy;
                break;
        }

        AfterWrite(segments, existed, oldValue, true, copy);
    }

    /// <summary>Removes the value at <paramref name="path"/>. Missing paths are a no-op.</summary>
    public void Delete(string path)
    {
        var segments = JsonPointer.Parse(path);
        if (!TryGet(segments, out var oldValue))
        {
            return;
        }

        if (segments.Count == 0)
        {
            _root = new JsonObject();
            AfterWrite(segments, true, oldValue, true, _root);
            return;
        }

        TryGet(segments.Take(segments.Count - 1).ToList(), out var parent);
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(last);
                break;
            case JsonArray arr when TryParseIndex(last, out var index) && index < arr.Count:
                // Detach the old node first so it can still be inspected for observers.
                arr[index] = null;
                arr.RemoveAt(index);
                break;
            default:
                return;
        }

        AfterWrite(segments, true, oldValue, false, null);
    }

    /// <summary>
    /// Observes <paramref name="path"/>. The callback fires when the value at that path
    /// changes, whether the write was at the path, above it, or below it.
    /// </summary>
    public IDisposable Observe(string path, Action callback)
    {
        var observer = new Observer(this, JsonPointer.Parse(path), callback);
        _observers.Add(observer);
        return observer;
    }

    /// <summary>Detaches every observer.</summary>
    public void DisposeObservers() => _observers.Clear();

    /// <summary>Returns a deep copy of the whole model.</summary>
    public JsonNode? Snapshot() => _root?.DeepClone();

    private JsonNode Vivify(IReadOnlyList<string> segments)
    {
        var path = JsonPointer.Format(segments);
        if (_root is null)
        {
            _root = new JsonObject();
        }

        if (_root is not JsonObject && _root is not JsonArray)
        {
            throw new A2UIDataException($"Cannot set path '{path}': the root is not a container.");
        }

        var current = _root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            var nextIsIndex = TryParseIndex(segments[i + 1], out _);
            switch (current)
            {
                case JsonObject obj:
                {
                    obj.TryGetPropertyValue(segment, out var child);
                    if (child is null)
                    {
                        child = nextIsIndex ? new JsonArray() : new JsonObject();
                        obj[segment] = child;
                    }
                    else if (child is not JsonObject && child is not JsonArray)
                    {
                        throw new A2UIDataException($"Cannot set path '{path}': '{segment}' holds a primitive value.");
                    }

                    current = child;
                    break;
                }
                case JsonArray arr:
                {
                    var index = ParseWriteIndex(segment, path);
                    while (arr.Count <= index)
                    {
                        arr.Add(null);
                    }

                    var child = arr[index];
                    if (child is null)
                    {
                        child = nextIsIndex ? new JsonArray() : new JsonObject();
                        arr[index] = child;
                    }
                    else if (child is not JsonObject && child is not JsonArray)
                    {
                        throw new A2UIDataException($"Cannot set path '{path}': index {index} holds a primitive value.");
                    }

                    current = child;
                    break;
                }
            }
        }

        if (current is JsonArray && !TryParseIndex(segments[^1], out _))
        {
            throw new A2UIDataException($"Cannot set path '{path}': non-numeric segment '{segments[^1]}' addresses a list.");
        }

        return current;
    }

    private static int ParseWriteIndex(string segment, string path)
    {
        if (!TryParseIndex(segment, out var index))
        {
            throw new A2UIDataException($"Cannot set path '{path}': non-numeric segment '{segment}' addresses a list.");
        }

        if (index > MaxListIndex)
        {
            throw new A2UIDataException($"Cannot set path '{path}': list index {index} exceeds {MaxListIndex}.");
        }

        return index;
    }

    internal static bool TryParseIndex(string segment, out int index)
    {
        index = -1;
        if (segment.Length == 0 || segment.Length > 9 || (segment.Length > 1 && segment[0] == '0'))
        {
            if (segment.Length > 9 && segment.All(char.IsAsciiDigit) && segment[0] != '0')
            {
                index = int.MaxValue;
                return true;
            }

            return false;
        }

        foreach (var c in segment)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        index = int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private void AfterWrite(IReadOnlyList<string> segments, bool existed, JsonNode? oldValue, bool exists, JsonNode? newValue)
    {
        if (existed == exists && JsonNode.DeepEquals(oldValue, newValue))
        {
            return;
        }

        foreach (var observer in _observers.ToArray())
        {
            if (JsonPointer.IsSameOrAncestor(observer.Segments, segments))
            {
                observer.Callback();
            }
            else if (JsonPointer.IsSameOrAncestor(segments, observer.Segments))
            {
                var relative = observer.Segments.Skip(segments.Count).ToList();
                var hadOld = existed & TryGet(oldValue, relative, out var oldChild);
                var hasNew = exists & TryGet(newValue, relative, out var newChild);
                if (hadOld != hasNew || !JsonNode.DeepEquals(oldChild, newChild))
                {
                    observer.Callback();
                }
            }
        }

        Changed?.Invoke(JsonPointer.Format(segments));
    }

    private sealed class Observer(DataModel owner, IReadOnlyList<string> segments, Action callback) : IDisposable
    {
        public IReadOnlyList<string> Segments { get; } = segments;

        public Action Callback { get; } = callback;

        public void Dispose() => owner._observers.Remove(this);
    }
}
