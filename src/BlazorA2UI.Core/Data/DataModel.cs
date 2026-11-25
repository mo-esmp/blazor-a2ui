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
    public JsonNode? Get(string path) => TryGet(path, out JsonNode? value) ? value : null;

    internal bool TryGet(IReadOnlyList<string> segments, out JsonNode? value) => TryGet(_root, segments, out value);

    private static bool TryGet(JsonNode? start, IReadOnlyList<string> segments, out JsonNode? value)
    {
        JsonNode? current = start;
        foreach (string segment in segments)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out JsonNode? child):
                    current = child;
                    break;
                case JsonArray arr when TryParseIndex(segment, out int index) && index < arr.Count:
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
        IReadOnlyList<string> segments = JsonPointer.Parse(path);
        JsonNode? copy = value?.DeepClone();
        TryGet(segments, out JsonNode? oldValue);
        bool existed = TryGet(segments, out _);

        if (segments.Count == 0)
        {
            _root = copy;
            AfterWrite(segments, existed, oldValue, true, copy);
            return;
        }

        CheckWritable(segments, path);
        JsonNode parent = Vivify(segments);
        string last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = copy;
                break;
            case JsonArray arr:
                int index = ParseWriteIndex(last, path);
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
        IReadOnlyList<string> segments = JsonPointer.Parse(path);
        if (!TryGet(segments, out JsonNode? oldValue))
        {
            return;
        }

        if (segments.Count == 0)
        {
            _root = new JsonObject();
            AfterWrite(segments, true, oldValue, true, _root);
            return;
        }

        TryGet(segments.Take(segments.Count - 1).ToList(), out JsonNode? parent);
        string last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(last);
                break;
            case JsonArray arr when TryParseIndex(last, out int index) && index < arr.Count:
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
        Observer observer = new(this, JsonPointer.Parse(path), callback);
        _observers.Add(observer);
        return observer;
    }

    /// <summary>Detaches every observer.</summary>
    public void DisposeObservers() => _observers.Clear();

    /// <summary>Returns a deep copy of the whole model.</summary>
    public JsonNode? Snapshot() => _root?.DeepClone();

    /// <summary>
    /// Walks the path without changing anything and throws when the write would fail, so a
    /// rejected write never leaves half-created containers behind.
    /// </summary>
    private void CheckWritable(IReadOnlyList<string> segments, string path)
    {
        if (_root is not null and not JsonObject and not JsonArray)
        {
            throw new A2UIDataException($"Cannot set path '{path}': the root is not a container.");
        }

        JsonNode? current = _root ?? new JsonObject();
        bool creating = false; // true once the walk has left the existing data
        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i];
            bool isLast = i == segments.Count - 1;
            if (creating)
            {
                // A created container is a list exactly when its segment is an index.
                if (TryParseIndex(segment, out _))
                {
                    ParseWriteIndex(segment, path);
                }

                continue;
            }

            JsonNode? child = null;
            switch (current)
            {
                case JsonObject obj:
                    obj.TryGetPropertyValue(segment, out child);
                    break;
                case JsonArray arr:
                    int index = ParseWriteIndex(segment, path);
                    child = index < arr.Count ? arr[index] : null;
                    break;
            }

            if (isLast)
            {
                break;
            }

            if (child is null)
            {
                creating = true;
                continue;
            }

            if (child is not JsonObject && child is not JsonArray)
            {
                throw new A2UIDataException($"Cannot set path '{path}': '{segment}' holds a primitive value.");
            }

            current = child;
        }
    }

    private JsonNode Vivify(IReadOnlyList<string> segments)
    {
        string path = JsonPointer.Format(segments);
        if (_root is null)
        {
            _root = new JsonObject();
        }

        if (_root is not JsonObject && _root is not JsonArray)
        {
            throw new A2UIDataException($"Cannot set path '{path}': the root is not a container.");
        }

        JsonNode current = _root;
        for (int i = 0; i < segments.Count - 1; i++)
        {
            string segment = segments[i];
            bool nextIsIndex = TryParseIndex(segments[i + 1], out _);
            switch (current)
            {
                case JsonObject obj:
                    {
                        obj.TryGetPropertyValue(segment, out JsonNode? child);
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
                        int index = ParseWriteIndex(segment, path);
                        while (arr.Count <= index)
                        {
                            arr.Add(null);
                        }

                        JsonNode? child = arr[index];
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
        if (!TryParseIndex(segment, out int index))
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

        foreach (char c in segment)
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

        foreach (Observer observer in _observers.ToArray())
        {
            if (JsonPointer.IsSameOrAncestor(observer.Segments, segments))
            {
                observer.Callback();
            }
            else if (JsonPointer.IsSameOrAncestor(segments, observer.Segments))
            {
                List<string> relative = observer.Segments.Skip(segments.Count).ToList();
                bool hadOld = existed & TryGet(oldValue, relative, out JsonNode? oldChild);
                bool hasNew = exists & TryGet(newValue, relative, out JsonNode? newChild);
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
