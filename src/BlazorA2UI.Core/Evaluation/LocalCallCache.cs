using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// Keeps the asynchronous local function calls that a surface's bindings and checks started, so that
/// the next evaluation after a call settles reads its value or error instead of running it again.
/// </summary>
/// <remarks>
/// Entries are keyed by catalog, function name and canonical arguments, so a change in any input
/// selects a different entry. Calls that complete synchronously are never stored.
/// </remarks>
internal sealed class LocalCallCache
{
    /// <summary>
    /// Maximum number of entries one surface keeps.
    /// </summary>
    internal const int MaxEntries = 256;

    /// <summary>
    /// Guards <see cref="_entries"/>, which evaluations and continuations reach from any thread.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The stored calls, by cache key.
    /// </summary>
    private readonly Dictionary<string, Task<JsonNode?>> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the number of stored calls.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Looks up a stored call.
    /// </summary>
    /// <returns>True with the call's task, which may still be running; false when no call is stored for the key.</returns>
    internal bool TryGet(string key, [MaybeNullWhen(false)] out Task<JsonNode?> task)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out task);
        }
    }

    /// <summary>
    /// Stores a call that has not completed yet. When the cache is full, settled entries are dropped
    /// first; if it is still full, the call is not stored and the next evaluation runs it again.
    /// </summary>
    internal void Add(string key, Task<JsonNode?> task)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                List<string> settled = [];
                foreach (KeyValuePair<string, Task<JsonNode?>> entry in _entries)
                {
                    if (entry.Value.IsCompleted)
                    {
                        settled.Add(entry.Key);
                    }
                }

                foreach (string settledKey in settled)
                {
                    _entries.Remove(settledKey);
                }

                if (_entries.Count >= MaxEntries)
                {
                    return;
                }
            }

            _entries[key] = task;
        }
    }

    /// <summary>
    /// Drops every stored call, for example when the surface is deleted.
    /// </summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
