using System.Text.Json.Nodes;

namespace BlazorA2UI.Data;

/// <summary>
/// The reactive data model of one surface: a JSON document addressed with JSON
/// Pointers. Writes create missing containers (a list when the next segment is a
/// numeric index, an object otherwise) and notify observers whose value changed.
/// </summary>
public sealed class DataModel
{
    /// <summary>
    /// Largest list index a write may address.
    /// </summary>
    public const int MaxListIndex = 10_000;

    /// <summary>
    /// The registered path observers, in registration order.
    /// </summary>
    private readonly List<Observer> _observers = [];
    /// <summary>
    /// The model's root value, or null before anything is written.
    /// </summary>
    private JsonNode? _root;

    /// <summary>
    /// Creates a data model.
    /// </summary>
    /// <param name="initial">The initial root value; a defensive copy is stored. Null starts with an empty model.</param>
    public DataModel(JsonNode? initial = null)
    {
        _root = initial?.DeepClone() ?? new JsonObject();
    }

    /// <summary>
    /// Raised after every write that changed the model, with the canonical path written.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>
    /// The root value. Treat as read-only; write with <see cref="Set"/>.
    /// </summary>
    public JsonNode? Root => _root;

    /// <summary>
    /// Reads the value at <paramref name="path"/>.
    /// </summary>
    /// <returns>True when the path holds a value (which may be JSON null).</returns>
    public bool TryGet(string path, out JsonNode? value) => TryGet(JsonPointer.Parse(path), out value);

    /// <summary>
    /// Reads the value at <paramref name="path"/>, or null when absent.
    /// </summary>
    public JsonNode? Get(string path) => TryGet(path, out JsonNode? value) ? value : null;

    /// <summary>
    /// Reads the value at already-parsed pointer segments.
    /// </summary>
    /// <returns>False when the path does not exist; <paramref name="value"/> is then null.</returns>
    internal bool TryGet(IReadOnlyList<string> segments, out JsonNode? value) => TryGet(_root, segments, out value);

    /// <summary>
    /// Walks <paramref name="segments"/> from <paramref name="start"/>.
    /// </summary>
    /// <returns>False when a segment is missing or addresses past the end of a list.</returns>
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

    /// <summary>
    /// Removes the value at <paramref name="path"/>. Missing paths are a no-op.
    /// </summary>
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
                {
                    // Removing an item shifts every later item down, so the change is reported for the
                    // whole list: observers of later indices see their new values.
                    JsonArray before = (JsonArray)arr.DeepClone();
                    arr.RemoveAt(index);
                    List<string> listPath = segments.Take(segments.Count - 1).ToList();
                    AfterWrite(listPath, true, before, true, arr);
                    return;
                }
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

    /// <summary>
    /// Detaches every observer.
    /// </summary>
    public void DisposeObservers() => _observers.Clear();

    /// <summary>
    /// Returns a deep copy of the whole model.
    /// </summary>
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

    /// <summary>
    /// Creates the missing containers along the path and returns the parent of the last segment. Call <see cref="CheckWritable"/> first, so a failing write never leaves containers behind.
    /// </summary>
    /// <exception cref="A2UIDataException">A segment runs into a primitive value, or a non-numeric segment addresses a list.</exception>
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

    /// <summary>
    /// Parses a list index for a write.
    /// </summary>
    /// <exception cref="A2UIDataException">The segment is not a list index, or it is larger than <see cref="MaxListIndex"/>.</exception>
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

    /// <summary>
    /// Parses a canonical, non-negative list index (digits only, no leading zeros). A digit string too long for an <see cref="int"/> gives <see cref="int.MaxValue"/>, so it fails the write limit instead of parsing as a key.
    /// </summary>
    /// <returns>False when the segment is not such an index.</returns>
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

    /// <summary>
    /// Notifies the observers whose value changed with a write and raises <see cref="Changed"/>. Nothing is raised when the value at the path is unchanged.
    /// </summary>
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

    /// <summary>
    /// One path subscription made with <see cref="Observe"/>; disposing it unsubscribes.
    /// </summary>
    private sealed class Observer(DataModel owner, IReadOnlyList<string> segments, Action callback) : IDisposable
    {
        /// <summary>
        /// Gets the observed path, as pointer segments.
        /// </summary>
        public IReadOnlyList<string> Segments { get; } = segments;

        /// <summary>
        /// Gets the callback to run when the value at the path changes.
        /// </summary>
        public Action Callback { get; } = callback;

        /// <summary>
        /// Removes this subscription from its model.
        /// </summary>
        public void Dispose() => owner._observers.Remove(this);
    }
}
