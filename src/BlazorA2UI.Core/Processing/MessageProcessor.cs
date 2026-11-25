using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Text;

namespace BlazorA2UI.Processing;

/// <summary>
/// The A2UI v1.0 renderer state machine. Feed it agent-to-renderer messages
/// (<see cref="ProcessLine"/> for JSONL, <see cref="Process(JsonNode)"/> for parsed JSON);
/// it maintains surfaces, answers <c>callRendererFunction</c>, and emits renderer-to-agent
/// messages through <see cref="Outbound"/>.
/// </summary>
public sealed class MessageProcessor
{
    private static readonly string[] MessageKinds =
        ["createSurface", "updateComponents", "updateDataModel", "deleteSurface", "callRendererFunction", "agentFunctionResponse"];

    private readonly ConcurrentDictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedSurfaceIds = new(StringComparer.Ordinal);
    private readonly List<Task> _inFlight = [];
    private A2UIValidationConfig? _activeValidation;

    public MessageProcessor(IEnumerable<Catalog> catalogs, A2UIProcessorOptions? options = null)
    {
        Options = options ?? new A2UIProcessorOptions();
        Catalogs = new CatalogRegistry(catalogs);
        foreach (var catalog in Catalogs.Catalogs)
        {
            if (catalog.ProtocolVersion != A2UIProtocol.Version)
            {
                throw new A2UICatalogException(
                    $"Catalog '{catalog.CatalogId}' targets protocol {catalog.ProtocolVersion}; this renderer implements {A2UIProtocol.Version}.");
            }
        }

        Evaluator = new A2UIEvaluator(Catalogs, Options.Services);
        _activeValidation = Options.Validation;
    }

    /// <summary>Registers another catalog. Its id must not already be registered.</summary>
    public void AddCatalog(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.ProtocolVersion != A2UIProtocol.Version)
        {
            throw new A2UICatalogException(
                $"Catalog '{catalog.CatalogId}' targets protocol {catalog.ProtocolVersion}; this renderer implements {A2UIProtocol.Version}.");
        }

        Catalogs.Add(catalog);
    }

    public A2UIProcessorOptions Options { get; }

    public CatalogRegistry Catalogs { get; }

    public A2UIEvaluator Evaluator { get; }

    public IReadOnlyDictionary<string, Surface> Surfaces => _surfaces;

    /// <summary>
    /// Sends renderer-to-agent messages. Set this to your transport; without it, actions and
    /// agent function calls cannot reach the agent.
    /// </summary>
    public Func<A2UIOutboundMessage, ValueTask>? Outbound { get; set; }

    internal bool HasOutbound => Outbound is not null;

    public event Action<Surface>? SurfaceCreated;

    public event Action<Surface>? SurfaceDeleted;

    /// <summary>Raised for every error the renderer reports.</summary>
    public event Action<A2UIErrorReport>? ErrorReported;

    /// <summary>The <c>a2uiRendererCapabilities</c> object to advertise in transport metadata.</summary>
    public JsonObject GetRendererCapabilities() => new()
    {
        [A2UIProtocol.Version] = new JsonObject
        {
            ["supportedCatalogIds"] = new JsonArray(Catalogs.Catalogs.Select(c => (JsonNode)c.CatalogId).ToArray()),
        },
    };

    /// <summary>The <c>a2uiRendererDataModel</c> metadata for surfaces that asked for <c>sendDataModel</c>, or null.</summary>
    public JsonObject? GetRendererDataModel()
    {
        var surfaces = new JsonObject();
        foreach (var surface in _surfaces.Values.Where(s => s.SendDataModel).OrderBy(s => s.SurfaceId, StringComparer.Ordinal))
        {
            surfaces[surface.SurfaceId] = surface.DataModelSnapshot();
        }

        return surfaces.Count == 0 ? null : new JsonObject { ["version"] = A2UIProtocol.Version, ["surfaces"] = surfaces };
    }

    /// <summary>Processes one line of a JSONL stream. Blank lines are ignored.</summary>
    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            ReportError(new A2UIValidationException($"The message is not valid JSON: {ex.Message}"), null);
            return;
        }

        Process(node);
    }

    /// <summary>
    /// Processes a message, a JSON array of messages, or a <c>{"messages": [...]}</c> wrapper.
    /// Errors are reported through <see cref="ErrorReported"/> (and to the agent), never thrown.
    /// </summary>
    public void Process(JsonNode? message)
    {
        try
        {
            ProcessOrThrow(message);
        }
        catch (A2UIException ex)
        {
            ReportError(ex, ex.SurfaceId);
        }
    }

    /// <summary>Like <see cref="Process(JsonNode)"/>, but throws the first envelope-level error.</summary>
    public void ProcessOrThrow(JsonNode? message) => ProcessOrThrow(message, null);

    /// <summary>
    /// Like <see cref="ProcessOrThrow(JsonNode)"/>, with completeness rules for this call only
    /// (null uses <see cref="A2UIProcessorOptions.Validation"/>).
    /// </summary>
    public void ProcessOrThrow(JsonNode? message, A2UIValidationConfig? validation)
    {
        var previous = _activeValidation;
        _activeValidation = validation ?? Options.Validation;
        try
        {
            ProcessCore(message);
        }
        finally
        {
            _activeValidation = previous;
        }
    }

    private void ProcessCore(JsonNode? message)
    {
        switch (message)
        {
            case JsonArray batch:
                foreach (var item in batch)
                {
                    ProcessCore(item);
                }

                return;
            case JsonObject wrapper when wrapper.Count == 1 && wrapper["messages"] is JsonArray messages:
                ProcessCore(messages);
                return;
            case JsonObject envelope:
                CheckDepthAndPaths(envelope, 0);
                ProcessEnvelope(envelope);
                return;
            default:
                throw new A2UIValidationException("A message must be a JSON object.");
        }
    }

    /// <summary>Largest JSON nesting accepted in one message.</summary>
    internal const int MaxMessageDepth = 50;

    /// <summary>
    /// Rejects messages nested deeper than <see cref="MaxMessageDepth"/> and data paths
    /// (<c>@path</c> and <c>path</c> strings) that are not JSON Pointers or relative paths.
    /// </summary>
    private static void CheckDepthAndPaths(JsonNode? node, int depth)
    {
        if (depth > MaxMessageDepth)
        {
            throw new A2UIException(A2UIErrorCodes.RecursionLimitExceeded, $"Global recursion limit exceeded: Depth > {MaxMessageDepth}");
        }

        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    CheckDepthAndPaths(item, depth + 1);
                }

                break;
            case JsonObject obj:
                foreach (var key in new[] { "path", "@path" })
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var path) && !IsValidPathSyntax(path))
                    {
                        throw new A2UIValidationException($"Invalid path syntax: '{path}'") { Path = "/" + key };
                    }
                }

                foreach (var (_, value) in obj)
                {
                    CheckDepthAndPaths(value, depth + 1);
                }

                break;
        }
    }

    /// <summary>A JSON Pointer or relative path whose <c>~</c> characters are all <c>~0</c> or <c>~1</c> escapes.</summary>
    private static bool IsValidPathSyntax(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '~' && (i + 1 >= path.Length || path[i + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Completes when every in-flight <c>callRendererFunction</c> has been answered.</summary>
    public Task WhenIdleAsync()
    {
        Task[] tasks;
        lock (_inFlight)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            tasks = [.. _inFlight];
        }

        return Task.WhenAll(tasks);
    }

    private void ProcessEnvelope(JsonObject envelope)
    {
        if (!envelope.TryGetPropertyValue("version", out var versionNode))
        {
            throw new A2UIValidationException("The message has no 'version' field.");
        }

        var version = A2UIValues.AsString(versionNode);
        if (version != A2UIProtocol.Version)
        {
            throw new A2UIValidationException($"Unsupported protocol version '{versionNode?.ToJsonString()}'; this renderer implements {A2UIProtocol.Version}.");
        }

        var kinds = envelope.Select(p => p.Key).Where(k => k != "version").ToList();
        if (kinds.Count > 1)
        {
            throw new A2UIValidationException("Message contains multiple conflicting update actions.");
        }

        if (kinds.Count == 0)
        {
            throw new A2UIValidationException("The message has no action.");
        }

        var kind = kinds[0];
        if (!MessageKinds.Contains(kind))
        {
            throw new A2UIValidationException($"action '{kind}' is not supported in protocol version {A2UIProtocol.Version}.");
        }

        if (envelope[kind] is not JsonObject body)
        {
            throw new A2UIValidationException($"'{kind}' must be an object.");
        }

        switch (kind)
        {
            case "createSurface":
                CreateSurface(body);
                break;
            case "updateComponents":
                UpdateComponents(body);
                break;
            case "updateDataModel":
                UpdateDataModel(body);
                break;
            case "deleteSurface":
                DeleteSurface(body);
                break;
            case "callRendererFunction":
                ValidateCallRendererFunction(body);
                Track(HandleCallRendererFunctionAsync(body));
                break;
            case "agentFunctionResponse":
                HandleAgentFunctionResponse(body);
                break;
        }
    }

    private static string ReadSurfaceId(JsonObject body)
    {
        if (!body.TryGetPropertyValue("surfaceId", out var node) || A2UIValues.AsString(node) is not { } id)
        {
            throw new A2UIValidationException("surfaceId must be a string.") { SurfaceId = string.Empty, Path = "/surfaceId" };
        }

        if (id.Length == 0)
        {
            throw new A2UIValidationException("surfaceId must not be empty.") { SurfaceId = string.Empty, Path = "/surfaceId" };
        }

        return id;
    }

    private Surface GetLiveSurface(string surfaceId) =>
        _surfaces.TryGetValue(surfaceId, out var surface)
            ? surface
            : throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface '{surfaceId}' does not exist.") { SurfaceId = surfaceId };

    private void CreateSurface(JsonObject body)
    {
        var surfaceId = ReadSurfaceId(body);
        foreach (var (key, _) in body)
        {
            if (key is not ("surfaceId" or "catalogId" or "sendDataModel" or "components" or "dataModel" or "metadata" or "theme"))
            {
                throw new A2UIValidationException($"createSurface does not accept '{key}'.") { SurfaceId = surfaceId };
            }
        }

        if (body.ContainsKey("theme") && _activeValidation is not null)
        {
            throw new A2UIValidationException("createSurface 'theme' was removed in protocol 1.0.") { SurfaceId = surfaceId, Path = "/theme" };
        }

        string? catalogId = null;
        if (body.ContainsKey("catalogId"))
        {
            catalogId = A2UIValues.AsString(body["catalogId"]) is { Length: > 0 } id
                ? id
                : throw new A2UIValidationException("catalogId must be a non-empty string.") { SurfaceId = surfaceId };
            if (!Catalogs.TryGet(catalogId, out _))
            {
                throw new A2UICatalogException($"Catalog not found: {catalogId}") { SurfaceId = surfaceId };
            }
        }

        var metadata = ReadMetadata(body["metadata"], surfaceId, "createSurface.metadata");
        if (body["dataModel"] is { } dm && dm is not JsonObject)
        {
            throw new A2UIValidationException("dataModel must be an object.") { SurfaceId = surfaceId };
        }

        lock (_usedSurfaceIds)
        {
            if (_surfaces.ContainsKey(surfaceId))
            {
                throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface '{surfaceId}' already exists.") { SurfaceId = surfaceId };
            }

            _usedSurfaceIds.Add(surfaceId);
        }

        var surface = new Surface(
            this,
            surfaceId,
            catalogId,
            body["sendDataModel"] is JsonValue sdm && sdm.GetValueKind() == JsonValueKind.True,
            metadata);

        using (surface.BatchChanges())
        {
            if (body["dataModel"] is JsonObject dataModel)
            {
                surface.DataModel.Set("/", dataModel);
            }

            _surfaces[surfaceId] = surface;
            if (body["components"] is { } components)
            {
                ApplyComponents(surface, components);
            }
        }

        SurfaceCreated?.Invoke(surface);
    }

    private void UpdateComponents(JsonObject body)
    {
        var surfaceId = ReadSurfaceId(body);
        var surface = GetLiveSurface(surfaceId);
        if (body["components"] is not { } components)
        {
            throw new A2UIValidationException("updateComponents needs a components list.") { SurfaceId = surfaceId };
        }

        ApplyComponents(surface, components);
    }

    private void ApplyComponents(Surface surface, JsonNode components)
    {
        if (components is not JsonArray list || list.Count == 0)
        {
            throw new A2UIValidationException("components must be a non-empty array.") { SurfaceId = surface.SurfaceId };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<ComponentInstance>();
        var parseFailed = false;
        var batchIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var path = $"/components/{i}";
            if (list[i] is not JsonObject raw)
            {
                ReportValidation(surface.SurfaceId, path, "A component must be an object.");
                continue;
            }

            if (A2UIValues.AsString(raw["id"]) is { } rawId)
            {
                batchIndex.TryAdd(rawId, i);
            }

            if (A2UIValues.AsString(raw["id"]) is { } id && !seen.Add(id))
            {
                throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Duplicate component id '{id}' in one update.")
                {
                    SurfaceId = surface.SurfaceId,
                };
            }

            try
            {
                parsed.Add(ComponentParser.Parse(raw, surface, Catalogs, _activeValidation));
            }
            catch (A2UIException ex)
            {
                parseFailed = true;
                ReportValidation(surface.SurfaceId, path + (ex.Path ?? string.Empty), ex.Message, ex.Code);
            }
        }

        var candidate = surface.SnapshotComponents();
        foreach (var component in parsed)
        {
            candidate[component.Id] = component;
        }

        if (candidate.Count > Options.MaxComponentsPerSurface)
        {
            throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface exceeds {Options.MaxComponentsPerSurface} components.")
            {
                SurfaceId = surface.SurfaceId,
            };
        }

        CompositionCheck(surface.SurfaceId, candidate, batchIndex);
        TopologyCheck(surface.SurfaceId, candidate);
        if (_activeValidation is { } validation && !parseFailed)
        {
            CompletenessCheck(surface.SurfaceId, candidate, validation);
        }

        IndexScopeCheck(surface.SurfaceId, candidate);
        surface.ReplaceComponents(parsed);

    }

    /// <summary>
    /// Rejects self-references, cycles reachable from the root, and trees deeper than
    /// <see cref="A2UIProcessorOptions.MaxTreeDepth"/>.
    /// </summary>
    private void TopologyCheck(string surfaceId, Dictionary<string, ComponentInstance> components)
    {
        foreach (var component in components.Values)
        {
            foreach (var (childId, pointer) in component.ChildReferences())
            {
                if (childId == component.Id)
                {
                    throw new A2UIException(
                        A2UIErrorCodes.RecursionLimitExceeded,
                        $"Self-reference detected: Component '{component.Id}' references itself in field '{pointer.TrimStart('/')}'")
                    {
                        SurfaceId = surfaceId,
                    };
                }
            }
        }

        if (!components.ContainsKey("root"))
        {
            return;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var stack = new Stack<(string Id, int Depth, IEnumerator<string> Children)>();
        state["root"] = 1;
        stack.Push(("root", 0, components["root"].StaticChildIds().GetEnumerator()));
        while (stack.Count > 0)
        {
            var (id, depth, children) = stack.Peek();
            if (!children.MoveNext())
            {
                state[id] = 2;
                stack.Pop();
                continue;
            }

            var childId = children.Current;
            if (!components.TryGetValue(childId, out var child))
            {
                continue; // Progressive rendering: the child may arrive later.
            }

            if (state.TryGetValue(childId, out var s) && s == 1)
            {
                throw new A2UIException(A2UIErrorCodes.RecursionLimitExceeded, $"Circular reference detected involving component '{childId}'")
                {
                    SurfaceId = surfaceId,
                };
            }

            if (s == 2)
            {
                continue;
            }

            if (depth + 1 > Options.MaxTreeDepth)
            {
                throw new A2UIException(
                    A2UIErrorCodes.RecursionLimitExceeded,
                    $"Global recursion limit exceeded: logical depth > {Options.MaxTreeDepth}")
                {
                    SurfaceId = surfaceId,
                };
            }

            state[childId] = 1;
            stack.Push((childId, depth + 1, child.StaticChildIds().GetEnumerator()));
        }
    }

    /// <summary>
    /// The strict-validation rules: a root must exist, references must resolve, and every
    /// component must be reachable from the root.
    /// </summary>
    private static void CompletenessCheck(string surfaceId, Dictionary<string, ComponentInstance> components, A2UIValidationConfig validation)
    {
        if (!validation.AllowMissingRoot && !components.ContainsKey("root"))
        {
            throw new A2UIException(A2UIErrorCodes.IntegrityError, "Missing root component: No component has id='root'") { SurfaceId = surfaceId };
        }

        if (!validation.AllowDanglingReferences)
        {
            foreach (var component in components.Values)
            {
                foreach (var (childId, pointer) in component.ChildReferences())
                {
                    if (!components.ContainsKey(childId))
                    {
                        throw new A2UIException(
                            A2UIErrorCodes.IntegrityError,
                            $"Dangling reference: Component '{component.Id}' references non-existent component '{childId}' in field '{FieldName(pointer)}'")
                        {
                            SurfaceId = surfaceId,
                        };
                    }
                }
            }
        }

        if (!validation.AllowOrphanComponents && !validation.AllowMissingRoot && components.ContainsKey("root"))
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { "root" };
            var queue = new Queue<string>();
            queue.Enqueue("root");
            while (queue.Count > 0)
            {
                foreach (var childId in components[queue.Dequeue()].StaticChildIds())
                {
                    if (components.ContainsKey(childId) && reached.Add(childId))
                    {
                        queue.Enqueue(childId);
                    }
                }
            }

            if (components.Keys.Where(id => !reached.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() is { } orphan)
            {
                throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Component '{orphan}' is not reachable from 'root'")
                {
                    SurfaceId = surfaceId,
                };
            }
        }
    }

    /// <summary>Formats <c>/slots/1/child</c> as <c>slots[1].child</c>.</summary>
    private static string FieldName(string pointer)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var segment in JsonPointer.Parse(pointer))
        {
            if (segment.All(char.IsAsciiDigit))
            {
                builder.Append('[').Append(segment).Append(']');
            }
            else
            {
                builder.Append(builder.Length == 0 ? string.Empty : ".").Append(segment);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rejects <c>@index</c> calls in components that are reachable from the root without passing
    /// through a collection template: there is no iteration scope for them to read.
    /// </summary>
    private static void IndexScopeCheck(string surfaceId, Dictionary<string, ComponentInstance> components)
    {
        if (!components.ContainsKey("root"))
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { "root" };
        var queue = new Queue<string>();
        queue.Enqueue("root");
        while (queue.Count > 0)
        {
            var component = components[queue.Dequeue()];
            foreach (var (key, value) in component.Properties)
            {
                if (ContainsIndexCall(value, 0))
                {
                    throw new A2UIValidationException(
                        $"Component '{component.Id}': '{key}': @index function can only be evaluated inside a collection template iteration scope.")
                    {
                        SurfaceId = surfaceId,
                    };
                }
            }

            foreach (var childId in component.StaticChildIds(includeTemplates: false))
            {
                if (components.ContainsKey(childId) && visited.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
    }

    private static bool ContainsIndexCall(JsonNode? node, int depth) => depth <= 64 && node switch
    {
        JsonObject o when A2UIValues.AsString(o["@call"]) == "@index" => true,
        JsonObject o => o.Any(p => ContainsIndexCall(p.Value, depth + 1)),
        JsonArray a => a.Any(i => ContainsIndexCall(i, depth + 1)),
        _ => false,
    };

    /// <summary>
    /// Enforces <c>allowedParents</c> / <c>allowedChildren</c> over the whole candidate surface
    /// (the surface is the parent of <c>root</c>). The first violation rejects the update.
    /// </summary>
    private static void CompositionCheck(string surfaceId, Dictionary<string, ComponentInstance> components, Dictionary<string, int> batchIndex)
    {
        var parents = new Dictionary<string, List<ComponentInstance>>(StringComparer.Ordinal);
        foreach (var parent in components.Values)
        {
            foreach (var childId in parent.StaticChildIds())
            {
                if (!parents.TryGetValue(childId, out var list))
                {
                    parents[childId] = list = [];
                }

                list.Add(parent);
            }
        }

        string Pointer(string id, string suffix) =>
            "/components/" + (batchIndex.TryGetValue(id, out var index) ? index.ToString(CultureInfo.InvariantCulture) : JsonPointer.Escape(id)) + suffix;

        static string Format(IReadOnlyList<string> names) => "[" + string.Join(", ", names.Select(n => $"'{n}'")) + "]";

        foreach (var component in components.Values)
        {
            if (component.Definition.AllowedParents is { Count: > 0 } allowedParents)
            {
                var toCheck = new List<(string Id, string Type)>();
                if (component.Id == "root")
                {
                    toCheck.Add((Composition.SurfaceType, Composition.SurfaceType));
                }

                toCheck.AddRange((parents.GetValueOrDefault(component.Id) ?? []).Select(p => (p.Id, p.Type)));
                foreach (var (parentId, parentType) in toCheck)
                {
                    if (!allowedParents.Contains(parentType))
                    {
                        throw new A2UIException(
                            A2UIErrorCodes.UnallowedParent,
                            $"Component '{component.Id}' ({component.Type}) cannot be placed under parent '{parentId}' ({parentType}). Allowed parents: {Format(allowedParents)}.")
                        {
                            SurfaceId = surfaceId,
                            Path = Pointer(component.Id, string.Empty),
                        };
                    }
                }
            }

            if (component.Definition.AllowedChildren is { Count: > 0 } allowedChildren)
            {
                foreach (var (childId, pointer) in component.ChildReferences())
                {
                    if (components.TryGetValue(childId, out var child) && !allowedChildren.Contains(child.Type))
                    {
                        throw new A2UIException(
                            A2UIErrorCodes.UnallowedChild,
                            $"Container '{component.Id}' ({component.Type}) cannot contain child '{childId}' ({child.Type}). Allowed children: {Format(allowedChildren)}.")
                        {
                            SurfaceId = surfaceId,
                            Path = Pointer(component.Id, pointer),
                        };
                    }
                }
            }
        }
    }

    private void UpdateDataModel(JsonObject body)
    {
        var surfaceId = ReadSurfaceId(body);
        var surface = GetLiveSurface(surfaceId);
        if (!body.TryGetPropertyValue("value", out var value))
        {
            throw new A2UIValidationException("updateDataModel needs a 'value' (use null to delete).") { SurfaceId = surfaceId };
        }

        var path = body.ContainsKey("path")
            ? A2UIValues.AsString(body["path"]) ?? throw new A2UIValidationException("path must be a string.") { SurfaceId = surfaceId, Path = "/path" }
            : "/";
        if (!path.StartsWith('/'))
        {
            throw new A2UIValidationException($"updateDataModel path '{path}' must be an absolute JSON Pointer.") { SurfaceId = surfaceId, Path = "/path" };
        }
        try
        {
            if (value is null && !IsListElement(surface.DataModel, path))
            {
                surface.DataModel.Delete(path);
            }
            else
            {
                surface.DataModel.Set(path, value);
            }
        }
        catch (A2UIException ex)
        {
            throw new A2UIException(ex.Code, ex.Message) { SurfaceId = surfaceId };
        }
    }

    /// <summary>A null written to a list element keeps the list's length (the element becomes null).</summary>
    private static bool IsListElement(DataModel model, string path)
    {
        var segments = JsonPointer.Parse(path);
        if (segments.Count == 0)
        {
            return false;
        }

        return model.TryGet(JsonPointer.Format(segments.Take(segments.Count - 1)), out var parent) && parent is JsonArray;
    }

    private void DeleteSurface(JsonObject body)
    {
        var surfaceId = ReadSurfaceId(body);
        if (!_surfaces.TryRemove(surfaceId, out var surface))
        {
            return;
        }

        surface.IsDeleted = true;
        surface.AgentCalls.CancelAll("The surface was deleted.");
        surface.DataModel.DisposeObservers();
        SurfaceDeleted?.Invoke(surface);
    }

    private void HandleAgentFunctionResponse(JsonObject body)
    {
        var id = A2UIValues.AsString(body["functionCallId"])
                 ?? throw new A2UIValidationException("agentFunctionResponse needs a functionCallId.");
        if (body.ContainsKey("value") == body.ContainsKey("error"))
        {
            throw new A2UIValidationException("agentFunctionResponse must have exactly one of 'value' or 'error'.") { FunctionCallId = id };
        }

        foreach (var surface in _surfaces.Values)
        {
            if (surface.AgentCalls.TryComplete(id, body))
            {
                return;
            }
        }

        // Unknown, late or duplicate responses are ignored.
    }

    /// <summary>Rejects a malformed <c>callRendererFunction</c> envelope before anything runs.</summary>
    private static void ValidateCallRendererFunction(JsonObject body)
    {
        var functionCallId = A2UIValues.AsString(body["functionCallId"]) is { Length: > 0 } id
            ? id
            : throw new A2UIValidationException("callRendererFunction needs a non-empty string functionCallId.") { Path = "/functionCallId" };

        A2UIException Invalid(string message) =>
            new(A2UIErrorCodes.InvalidFunctionCall, message) { FunctionCallId = functionCallId };

        if (body["callFunction"] is not JsonObject call)
        {
            throw Invalid("callRendererFunction needs a callFunction object.");
        }

        if (A2UIValues.AsString(call["@call"]) is not { Length: > 0 })
        {
            throw Invalid("callFunction needs a non-empty string '@call'.");
        }

        if (A2UIValues.AsString(call["catalogId"]) is not { Length: > 0 })
        {
            throw Invalid("callFunction needs a non-empty string 'catalogId'.");
        }

        if (call.ContainsKey("args") && call["args"] is not JsonObject)
        {
            throw Invalid("callFunction 'args' must be an object.");
        }
    }

    private async Task HandleCallRendererFunctionAsync(JsonObject body)
    {
        var functionCallId = A2UIValues.AsString(body["functionCallId"]);
        if (functionCallId is null)
        {
            ReportError(new A2UIException(A2UIErrorCodes.InvalidFunctionCall, "callRendererFunction needs a string functionCallId."), null);
            return;
        }

        try
        {
            if (body["callFunction"] is not JsonObject call
                || A2UIValues.AsString(call["@call"]) is not { Length: > 0 }
                || A2UIValues.AsString(call["catalogId"]) is not { Length: > 0 })
            {
                throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, "callFunction needs '@call' and 'catalogId'.");
            }

            EvaluationContext context;
            if (body.ContainsKey("surfaceId"))
            {
                var surfaceId = A2UIValues.AsString(body["surfaceId"]);
                if (surfaceId is null || !_surfaces.TryGetValue(surfaceId, out var surface))
                {
                    throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Surface '{surfaceId}' does not exist.");
                }

                context = surface.RootContext;
            }
            else
            {
                context = EvaluationContext.Detached();
            }

            var value = await Evaluator.EvaluateAsync(call, context, new EvaluationScope(EvaluationMode.AgentCall)).ConfigureAwait(false);
            await SendOutboundAsync(new JsonObject
            {
                ["version"] = A2UIProtocol.Version,
                ["rendererFunctionResponse"] = new JsonObject { ["functionCallId"] = functionCallId, ["value"] = value },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var code = ex is A2UIException { Code: A2UIErrorCodes.InvalidFunctionCall } ? A2UIErrorCodes.InvalidFunctionCall : A2UIErrorCodes.ExecutionError;
            await SendOutboundAsync(new JsonObject
            {
                ["version"] = A2UIProtocol.Version,
                ["rendererFunctionResponse"] = new JsonObject
                {
                    ["functionCallId"] = functionCallId,
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
                },
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Runs an action for a component after a user gesture.</summary>
    internal async Task DispatchActionAsync(
        Surface surface,
        ComponentInstance component,
        JsonNode? action,
        EvaluationContext context,
        CancellationToken cancellationToken)
    {
        if (action is not JsonObject actionObject)
        {
            return;
        }

        var scope = new EvaluationScope(EvaluationMode.Action) { CancellationToken = cancellationToken };
        try
        {
            if (actionObject["functionCall"] is JsonObject functionCall)
            {
                await Evaluator.EvaluateAsync(functionCall, context, scope).ConfigureAwait(false);
                return;
            }

            if (actionObject["event"] is not JsonObject ev || A2UIValues.AsString(ev["name"]) is not { } name)
            {
                throw new A2UIExpressionException("An action needs an 'event' with a name, or a 'functionCall'.");
            }

            var context2 = new JsonObject();
            if (ev["context"] is JsonObject rawContext)
            {
                // Entries resolve in order; the first failure stops the action.
                foreach (var (key, raw) in rawContext)
                {
                    try
                    {
                        context2[A2UIEvaluator.UnescapeKey(key)] = await Evaluator.EvaluateAsync(raw, context, scope).ConfigureAwait(false);
                    }
                    catch (A2UIException ex) when (ex.Code != A2UIErrorCodes.Cancelled)
                    {
                        throw new A2UIExpressionException($"Action event context entry '{key}' is invalid: {ex.Message}");
                    }
                }
            }
            else if (ev["context"] is not null)
            {
                throw new A2UIExpressionException("An action event's context must be an object.");
            }

            string? userMessage = null;
            if (ev["userMessage"] is { } rawMessage)
            {
                var resolved = await Evaluator.EvaluateAsync(rawMessage, context, scope).ConfigureAwait(false);
                if (resolved is not null && !(resolved is JsonValue sv && sv.GetValueKind() == JsonValueKind.String))
                {
                    throw new A2UIException(A2UIErrorCodes.InvalidAction, "An action event's userMessage must resolve to a string.");
                }

                userMessage = A2UIValues.AsString(resolved);
            }

            var body = new JsonObject
            {
                ["name"] = name,
                ["surfaceId"] = surface.SurfaceId,
                ["sourceComponentId"] = component.Id,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["context"] = context2,
            };
            if (userMessage is not null)
            {
                body["userMessage"] = userMessage;
            }

            await SendOutboundAsync(new JsonObject { ["version"] = A2UIProtocol.Version, ["action"] = body }).ConfigureAwait(false);
        }
        catch (A2UIException ex)
        {
            ReportError(new A2UIException(ex.Code, ex.Message), surface.SurfaceId);
        }
    }

    internal void ReportValidation(string surfaceId, string path, string message, string code = A2UIErrorCodes.ValidationFailed)
    {
        var wireCode = code is A2UIErrorCodes.UnallowedChild or A2UIErrorCodes.UnallowedParent or A2UIErrorCodes.CatalogError
            or A2UIErrorCodes.IntegrityError or A2UIErrorCodes.RecursionLimitExceeded
            ? code
            : A2UIErrorCodes.ValidationFailed;
        ReportError(new A2UIException(wireCode, message) { SurfaceId = surfaceId, Path = path }, surfaceId);
    }

    /// <summary>Reports an error locally and, when configured, to the agent.</summary>
    public void ReportError(A2UIException error, string? surfaceId)
    {
        if (error.Code == A2UIErrorCodes.Cancelled)
        {
            return; // A cancelled call was cancelled on purpose (surface deleted or calls cancelled).
        }

        var functionCallId = error.FunctionCallId;
        surfaceId = functionCallId is null ? surfaceId ?? error.SurfaceId : null;
        var report = new A2UIErrorReport(error.Code, error.Message, surfaceId, functionCallId, error.Path);
        ErrorReported?.Invoke(report);

        if (!Options.SendErrorsToAgent || !HasOutbound || (surfaceId is null && functionCallId is null))
        {
            return;
        }

        var body = new JsonObject { ["code"] = report.Code, ["message"] = report.Message };
        if (surfaceId is not null)
        {
            body["surfaceId"] = surfaceId;
        }

        if (functionCallId is not null)
        {
            body["functionCallId"] = functionCallId;
        }

        if (report.Code is A2UIErrorCodes.ValidationFailed or A2UIErrorCodes.UnallowedChild or A2UIErrorCodes.UnallowedParent)
        {
            body["path"] = report.Path ?? "/";
        }

        SendOutbound(new JsonObject { ["version"] = A2UIProtocol.Version, ["error"] = body });
    }

    internal void SendOutbound(JsonObject message) => Track(SendOutboundAsync(message));

    private async Task SendOutboundAsync(JsonObject message)
    {
        if (Outbound is not { } outbound)
        {
            return;
        }

        try
        {
            await outbound(new A2UIOutboundMessage(message, GetRendererDataModel() is { } dm
                ? new JsonObject { ["a2uiRendererDataModel"] = dm }
                : null)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorReported?.Invoke(new A2UIErrorReport(A2UIErrorCodes.ExecutionError, $"Sending a message to the agent failed: {ex.Message}", null, null, null));
        }
    }

    private void Track(Task task)
    {
        if (task.IsCompleted)
        {
            return;
        }

        lock (_inFlight)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            _inFlight.Add(task);
        }
    }

    internal static JsonObject? ReadMetadata(JsonNode? node, string surfaceId, string where)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject metadata)
        {
            throw new A2UIValidationException($"{where} must be an object.") { SurfaceId = surfaceId };
        }

        foreach (var (key, value) in metadata)
        {
            if (key != "extensions")
            {
                throw new A2UIValidationException($"{where} may only hold 'extensions', not '{key}'.") { SurfaceId = surfaceId };
            }

            if (value is not JsonObject extensions)
            {
                throw new A2UIValidationException($"{where}.extensions must be an object.") { SurfaceId = surfaceId };
            }

            foreach (var (extensionKey, _) in extensions)
            {
                if (!Uax31.IsIdentifier(extensionKey))
                {
                    throw new A2UIValidationException($"Extension key '{extensionKey}' is not a UAX #31 identifier.") { SurfaceId = surfaceId };
                }
            }
        }

        return (JsonObject)metadata.DeepClone();
    }
}
