using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
using BlazorA2UI.Rpc;
using BlazorA2UI.Text;

namespace BlazorA2UI.Processing;

/// <summary>
/// The A2UI v1.0 renderer state machine. Feed it agent-to-renderer messages
/// (<see cref="ProcessLine"/> for JSONL, <see cref="Process(JsonNode)"/> for parsed JSON);
/// it maintains surfaces, answers <c>callRendererFunction</c>, and emits renderer-to-agent
/// messages through <see cref="Outbound"/>.
/// </summary>
public sealed class MessageProcessor : IDisposable
{
    /// <summary>
    /// The protocol keys whose string values are data paths.
    /// </summary>
    private static readonly string[] s_pathKeys = ["path", "@path"];

    /// <summary>
    /// The message kinds of protocol v1.0; an envelope must hold exactly one of them.
    /// </summary>
    private static readonly string[] s_messageKinds =
        ["createSurface", "updateComponents", "updateDataModel", "deleteSurface", "callRendererFunction", "agentFunctionResponse"];

    /// <summary>
    /// The live surfaces, by id.
    /// </summary>
    private readonly ConcurrentDictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    /// <summary>
    /// The ids of surfaces that exist or are being created, so two <c>createSurface</c> messages cannot claim the same id.
    /// </summary>
    private readonly HashSet<string> _reservedSurfaceIds = new(StringComparer.Ordinal);
    /// <summary>
    /// Outbound sends that have not finished, so callers can wait for them.
    /// </summary>
    private readonly List<Task> _inFlight = [];
    /// <summary>
    /// Set by <see cref="Dispose"/> under the agent-call gate; read without a lock elsewhere because it only ever changes from false to true.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Guards the processor-wide set of agent call ids and the disposed flag, so an id is reserved once and
    /// disposal either rejects a new call or sees it registered and cancels it.
    /// </summary>
    private readonly object _agentCallGate = new();

    /// <summary>
    /// The ids of agent calls waiting for an answer on any surface.
    /// </summary>
    private readonly HashSet<string> _agentCallIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the lock every surface's agent calls share with the id reservations.
    /// </summary>
    internal object AgentCallGate => _agentCallGate;

    /// <summary>
    /// Creates a processor.
    /// </summary>
    /// <param name="catalogs">The catalogs the renderer supports.</param>
    /// <param name="options">Limits and behavior; null uses the defaults.</param>
    /// <remarks>
    /// Catalogs for another protocol version may be registered, but surfaces never see them, and an agent
    /// call that names one is refused with <see cref="A2UIErrorCodes.InvalidFunctionCall"/>.
    /// </remarks>
    /// <exception cref="A2UICatalogException">Two catalogs share an id.</exception>
    public MessageProcessor(IEnumerable<Catalog> catalogs, A2UIProcessorOptions? options = null)
    {
        Options = options ?? new A2UIProcessorOptions();
        Catalogs = new CatalogRegistry(catalogs);
        Evaluator = new A2UIEvaluator(Catalogs, Options.Services);
    }

    /// <summary>
    /// Registers another catalog. Its id must not already be registered. Surfaces created afterwards can use it;
    /// existing surfaces keep the catalogs they were created with.
    /// </summary>
    /// <exception cref="A2UICatalogException">A catalog with the same id is already registered; nothing changes.</exception>
    public void AddCatalog(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalogs.Add(catalog);
    }

    /// <summary>
    /// Gets the processor's limits and behavior.
    /// </summary>
    public A2UIProcessorOptions Options { get; }

    /// <summary>
    /// Gets the catalogs the renderer supports. A surface sees only the ones registered when it was created.
    /// </summary>
    public CatalogRegistry Catalogs { get; }

    /// <summary>
    /// Gets the evaluator for bindings and function calls.
    /// </summary>
    public A2UIEvaluator Evaluator { get; }

    /// <summary>
    /// Gets the live surfaces, by id.
    /// </summary>
    public IReadOnlyDictionary<string, Surface> Surfaces => _surfaces;

    /// <summary>
    /// Sends renderer-to-agent messages. Set this to your transport; without it, actions and
    /// agent function calls cannot reach the agent.
    /// </summary>
    public Func<A2UIOutboundMessage, ValueTask>? Outbound { get; set; }

    /// <summary>
    /// Gets whether a transport to the agent is set.
    /// </summary>
    internal bool HasOutbound => Outbound is not null;

    /// <summary>
    /// Raised after a surface is created, once its initial components and data are applied.
    /// </summary>
    public event Action<Surface>? SurfaceCreated;

    /// <summary>
    /// Raised after the agent deletes a surface.
    /// </summary>
    public event Action<Surface>? SurfaceDeleted;

    /// <summary>
    /// Raised for every error the renderer reports.
    /// </summary>
    public event Action<A2UIErrorReport>? ErrorReported;

    /// <summary>
    /// The <c>a2uiRendererCapabilities</c> object to advertise in transport metadata.
    /// </summary>
    public JsonObject GetRendererCapabilities() => new()
    {
        [A2UIProtocol.Version] = new JsonObject
        {
            ["supportedCatalogIds"] = new JsonArray(Catalogs.Catalogs
                .Where(c => c.ProtocolVersion == A2UIProtocol.Version)
                .Select(c => (JsonNode)c.CatalogId)
                .ToArray()),
        },
    };

    /// <summary>
    /// The <c>a2uiRendererDataModel</c> metadata for surfaces that asked for <c>sendDataModel</c>, or null.
    /// </summary>
    public JsonObject? GetRendererDataModel()
    {
        JsonObject surfaces = new();
        foreach (Surface? surface in _surfaces.Values.Where(s => s.SendDataModel).OrderBy(s => s.SurfaceId, StringComparer.Ordinal))
        {
            surfaces[surface.SurfaceId] = surface.DataModelSnapshot();
        }

        return surfaces.Count == 0 ? null : new JsonObject { ["version"] = A2UIProtocol.Version, ["surfaces"] = surfaces };
    }

    /// <summary>
    /// Parser options for stream lines: a repeated property name is a parse error, not a later exception.
    /// </summary>
    private static readonly JsonDocumentOptions s_lineOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Processes one line of a JSONL stream. Blank lines are ignored.
    /// </summary>
    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, documentOptions: s_lineOptions);
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

    /// <summary>
    /// Processes a message with settings for this call only. Errors are reported, never thrown.
    /// </summary>
    /// <param name="message">A message, a JSON array of messages, or a <c>{"messages": [...]}</c> wrapper.</param>
    /// <param name="context">The settings for this call.</param>
    public void Process(JsonNode? message, A2UIExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            ProcessWithContextOrThrow(message, context);
        }
        catch (A2UIException ex)
        {
            ReportError(ex, ex.SurfaceId);
        }
    }

    /// <summary>
    /// Like <see cref="Process(JsonNode)"/>, but throws the first envelope-level error.
    /// </summary>
    public void ProcessOrThrow(JsonNode? message) => ProcessOrThrow(message, null);

    /// <summary>
    /// Like <see cref="ProcessOrThrow(JsonNode)"/>, with settings for this call only.
    /// </summary>
    /// <param name="message">A message, a JSON array of messages, or a <c>{"messages": [...]}</c> wrapper.</param>
    /// <param name="context">The settings for this call.</param>
    /// <exception cref="A2UIException">The message is invalid; messages before it in a batch stay applied.</exception>
    public void ProcessWithContextOrThrow(JsonNode? message, A2UIExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessCore(message, new ProcessingState(context.Validation ?? Options.Validation, context.IsUserActivated));
    }

    /// <summary>
    /// Like <see cref="ProcessOrThrow(JsonNode)"/>, with completeness rules for this call only
    /// (null uses <see cref="A2UIProcessorOptions.Validation"/>).
    /// </summary>
    public void ProcessOrThrow(JsonNode? message, A2UIValidationConfig? validation) =>
        ProcessCore(message, new ProcessingState(validation ?? Options.Validation, UserActivated: false));

    /// <summary>
    /// Processes a message, a JSON array of messages, or a <c>{"messages": [...]}</c> wrapper.
    /// </summary>
    /// <exception cref="A2UIException">The message is invalid; messages before it in a batch stay applied.</exception>
    /// <param name="message">The message, batch or wrapper.</param>
    /// <param name="state">The settings of the entry point that received it; nested messages inherit them and nothing else does.</param>
    private void ProcessCore(JsonNode? message, ProcessingState state)
    {
        switch (message)
        {
            case JsonArray batch:
                foreach (JsonNode? item in batch)
                {
                    ProcessCore(item, state);
                }

                return;
            case JsonObject wrapper when PropertyCount(wrapper) == 1 && wrapper["messages"] is JsonArray messages:
                ProcessCore(messages, state);
                return;
            case JsonObject envelope:
                try
                {
                    CheckDepthAndPaths(envelope, 0);
                }
                catch (ArgumentException ex)
                {
                    throw DuplicateProperty(ex);
                }

                ProcessEnvelope(envelope, state);
                return;
            default:
                throw new A2UIValidationException("A message must be a JSON object.");
        }
    }

    /// <summary>
    /// Counts an object's properties. A node parsed with duplicate properties allowed builds its
    /// property table on first access, and a repeated name throws there.
    /// </summary>
    /// <exception cref="A2UIValidationException">The object repeats a property name.</exception>
    private static int PropertyCount(JsonObject obj)
    {
        try
        {
            return obj.Count;
        }
        catch (ArgumentException ex)
        {
            throw DuplicateProperty(ex);
        }
    }

    /// <summary>
    /// The validation error for a message that repeats a property name in one object.
    /// <see cref="CheckDepthAndPaths"/> touches every object of an envelope first, so this is where such a message stops.
    /// </summary>
    private static A2UIValidationException DuplicateProperty(ArgumentException ex) =>
        new($"The message repeats a property name in one object: {ex.Message}");

    /// <summary>
    /// Largest JSON nesting accepted in one message.
    /// </summary>
    internal const int MaxMessageDepth = 50;

    /// <summary>
    /// Rejects messages nested deeper than <see cref="MaxMessageDepth"/> and data paths
    /// (<c>@path</c> and <c>path</c> strings) that are not JSON Pointers or relative paths.
    /// Data-model values (<c>updateDataModel.value</c>, <c>createSurface.dataModel</c>) are only
    /// checked for depth, since their keys are application data.
    /// </summary>
    /// <param name="node">The value to check.</param>
    /// <param name="depth">Its depth below the envelope.</param>
    /// <param name="checkPaths">Whether <c>path</c> and <c>@path</c> keys here are protocol fields.</param>
    private static void CheckDepthAndPaths(JsonNode? node, int depth, bool checkPaths = true)
    {
        if (depth > MaxMessageDepth)
        {
            throw new A2UIException(A2UIErrorCodes.RecursionLimitExceeded, $"Global recursion limit exceeded: Depth > {MaxMessageDepth}");
        }

        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    CheckDepthAndPaths(item, depth + 1, checkPaths);
                }

                break;
            case JsonObject obj:
                foreach (string key in checkPaths ? s_pathKeys : [])
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out string? path) && !IsValidPathSyntax(path))
                    {
                        throw new A2UIValidationException($"Invalid path syntax: '{path}'") { Path = "/" + key };
                    }
                }

                foreach ((string key, JsonNode? value) in obj)
                {
                    // Data-model values are application data: a "path" key in them is ordinary text.
                    bool isData = depth == 1 && key is "value" or "dataModel";
                    CheckDepthAndPaths(value, depth + 1, checkPaths && !isData);
                }

                break;
        }
    }

    /// <summary>
    /// A JSON Pointer or relative path whose <c>~</c> characters are all <c>~0</c> or <c>~1</c> escapes.
    /// </summary>
    private static bool IsValidPathSyntax(string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '~' && (i + 1 >= path.Length || path[i + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether <see cref="Dispose"/> has run. A disposed processor sends no new agent calls.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Calls an agent function on behalf of the host (<c>callAgentFunction</c>) and waits for the agent's answer.
    /// </summary>
    /// <param name="surfaceId">The surface the call belongs to.</param>
    /// <param name="catalogId">The catalog that declares the function.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments to send, or null for none. They are copied.</param>
    /// <param name="options">The call id and timeout, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call; it then fails with <see cref="A2UIErrorCodes.Cancelled"/> and a late answer is ignored.</param>
    /// <returns>The value the agent answered with. A2UI errors fail the returned task rather than being thrown.</returns>
    /// <exception cref="ArgumentException">An id or the name is empty; a caller bug, thrown at once.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="A2UIAgentCallOptions.Timeout"/> is zero, negative (other than <see cref="Timeout.InfiniteTimeSpan"/>)
    /// or longer than <see cref="MaxAgentCallTimeout"/>; a caller bug, thrown at once and before anything is sent.
    /// </exception>
    /// <exception cref="A2UIException">
    /// No surface has the id (<see cref="A2UIErrorCodes.IntegrityError"/>); the call could not be sent
    /// (<see cref="A2UIErrorCodes.Disposed"/>, <see cref="A2UIErrorCodes.NoListener"/>, <see cref="A2UIErrorCodes.Duplicate"/>);
    /// it timed out (<see cref="A2UIErrorCodes.Timeout"/>) or was cancelled (<see cref="A2UIErrorCodes.Cancelled"/>);
    /// or the agent answered with an error, whose code it keeps.
    /// </exception>
    public Task<JsonNode?> CallAgentFunctionAsync(
        string surfaceId,
        string catalogId,
        string name,
        JsonObject? args = null,
        A2UIAgentCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(surfaceId);
        ArgumentException.ThrowIfNullOrEmpty(catalogId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (options?.FunctionCallId is { Length: 0 })
        {
            throw new ArgumentException("The function call id must not be empty.", nameof(options));
        }

        if (options?.Timeout is { } timeout && timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout > MaxAgentCallTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(options), timeout, $"The timeout must be positive and at most {MaxAgentCallTimeout}, or Timeout.InfiniteTimeSpan.");
        }

        if (_disposed)
        {
            return Task.FromException<JsonNode?>(new A2UIException(A2UIErrorCodes.Disposed, "The message processor was disposed."));
        }

        if (!_surfaces.TryGetValue(surfaceId, out Surface? surface))
        {
            return Task.FromException<JsonNode?>(
                new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface '{surfaceId}' does not exist.") { SurfaceId = surfaceId });
        }

        return surface.AgentCalls.CallForHostAsync(catalogId, name, (JsonObject?)args?.DeepClone() ?? new JsonObject(), options, cancellationToken);
    }

    /// <summary>
    /// Cancels every agent call waiting for an answer and refuses new ones with <see cref="A2UIErrorCodes.Disposed"/>.
    /// Surfaces and their data stay readable. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_agentCallGate)
        {
            if (_disposed)
            {
                return;
            }

            // Set under the gate: a call either reserved its id before this point and is cancelled below,
            // or tries afterwards and is refused.
            _disposed = true;
        }

        foreach (Surface surface in _surfaces.Values)
        {
            surface.AgentCalls.CancelAll("The message processor was disposed.");
        }
    }

    /// <summary>
    /// The longest timeout an agent call accepts, which is the longest a timer can wait.
    /// </summary>
    public static readonly TimeSpan MaxAgentCallTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Whether any surface has an agent call with this id waiting for an answer.
    /// </summary>
    internal bool IsAgentCallPending(string functionCallId)
    {
        lock (_agentCallGate)
        {
            return _agentCallIds.Contains(functionCallId);
        }
    }

    /// <summary>
    /// Reserves an agent call id for the whole processor and runs <paramref name="register"/> while the
    /// reservation is held, so no other call can take the id and disposal cannot slip in between.
    /// </summary>
    /// <param name="functionCallId">The id to reserve.</param>
    /// <param name="register">Registers the call on its surface; runs under the gate and must not call back into the processor.</param>
    /// <exception cref="A2UIException">
    /// The processor is disposed (<see cref="A2UIErrorCodes.Disposed"/>) or the id is taken
    /// (<see cref="A2UIErrorCodes.Duplicate"/>). Nothing is reserved. An exception from
    /// <paramref name="register"/> also releases the id and propagates.
    /// </exception>
    internal void ReserveAgentCall(string functionCallId, Action register)
    {
        lock (_agentCallGate)
        {
            if (_disposed)
            {
                throw new A2UIException(A2UIErrorCodes.Disposed, "The message processor was disposed.") { FunctionCallId = functionCallId };
            }

            if (!_agentCallIds.Add(functionCallId))
            {
                throw new A2UIException(A2UIErrorCodes.Duplicate, $"An agent call with id '{functionCallId}' is already waiting for an answer.")
                {
                    FunctionCallId = functionCallId,
                };
            }

            try
            {
                register();
            }
            catch
            {
                _agentCallIds.Remove(functionCallId);
                throw;
            }
        }
    }

    /// <summary>
    /// Releases an agent call id once its call has settled. Releasing an id that is not reserved does nothing.
    /// </summary>
    /// <param name="functionCallId">The id to release.</param>
    internal void ReleaseAgentCall(string functionCallId)
    {
        lock (_agentCallGate)
        {
            _agentCallIds.Remove(functionCallId);
        }
    }

    /// <summary>
    /// Completes when every in-flight <c>callRendererFunction</c> has been answered.
    /// </summary>
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

    /// <summary>
    /// Checks an envelope's version and kind and dispatches it.
    /// </summary>
    /// <exception cref="A2UIException">The envelope or its body is invalid.</exception>
    /// <param name="envelope">The message.</param>
    /// <param name="state">The settings of the entry point that received it.</param>
    private void ProcessEnvelope(JsonObject envelope, ProcessingState state)
    {
        if (!envelope.TryGetPropertyValue("version", out JsonNode? versionNode))
        {
            throw new A2UIValidationException("The message has no 'version' field.");
        }

        string? version = A2UIValues.AsString(versionNode);
        if (version != A2UIProtocol.Version)
        {
            throw new A2UIValidationException($"Unsupported protocol version '{versionNode?.ToJsonString()}'; this renderer implements {A2UIProtocol.Version}.");
        }

        List<string> kinds = envelope.Select(p => p.Key).Where(k => k != "version").ToList();
        if (kinds.Count > 1)
        {
            throw new A2UIValidationException("Message contains multiple conflicting update actions.");
        }

        if (kinds.Count == 0)
        {
            throw new A2UIValidationException("The message has no action.");
        }

        string kind = kinds[0];
        if (!s_messageKinds.Contains(kind))
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
                CreateSurface(body, state.Validation);
                break;
            case "updateComponents":
                UpdateComponents(body, state.Validation);
                break;
            case "updateDataModel":
                UpdateDataModel(body);
                break;
            case "deleteSurface":
                DeleteSurface(body);
                break;
            case "callRendererFunction":
                ValidateCallRendererFunction(body);
                Track(HandleCallRendererFunctionAsync(body, state.UserActivated));
                break;
            case "agentFunctionResponse":
                HandleAgentFunctionResponse(body);
                break;
        }
    }

    /// <summary>
    /// Reads a body's <c>surfaceId</c>.
    /// </summary>
    /// <exception cref="A2UIValidationException">The id is missing, not a string, or empty.</exception>
    private static string ReadSurfaceId(JsonObject body)
    {
        if (!body.TryGetPropertyValue("surfaceId", out JsonNode? node) || A2UIValues.AsString(node) is not { } id)
        {
            throw new A2UIValidationException("surfaceId must be a string.") { SurfaceId = string.Empty, Path = "/surfaceId" };
        }

        if (id.Length == 0)
        {
            throw new A2UIValidationException("surfaceId must not be empty.") { SurfaceId = string.Empty, Path = "/surfaceId" };
        }

        return id;
    }

    /// <summary>
    /// Returns the live surface with the id.
    /// </summary>
    /// <exception cref="A2UIException">No surface has the id (<see cref="A2UIErrorCodes.IntegrityError"/>).</exception>
    private Surface GetLiveSurface(string surfaceId) =>
        _surfaces.TryGetValue(surfaceId, out Surface? surface)
            ? surface
            : throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface '{surfaceId}' does not exist.") { SurfaceId = surfaceId };

    /// <summary>
    /// Handles <c>createSurface</c>. The surface is published only after its data and components applied without error, so a rejected message leaves no surface.
    /// </summary>
    /// <exception cref="A2UIException">The message is invalid or the id is taken.</exception>
    /// <param name="body">The <c>createSurface</c> body.</param>
    /// <param name="validation">The completeness rules for this message, or null for a streaming renderer.</param>
    private void CreateSurface(JsonObject body, A2UIValidationConfig? validation)
    {
        string surfaceId = ReadSurfaceId(body);
        foreach ((string key, JsonNode? _) in body)
        {
            if (key is not ("surfaceId" or "catalogId" or "sendDataModel" or "components" or "dataModel" or "metadata" or "theme"))
            {
                throw new A2UIValidationException($"createSurface does not accept '{key}'.") { SurfaceId = surfaceId };
            }
        }

        if (body.ContainsKey("theme") && validation is not null)
        {
            throw new A2UIValidationException("createSurface 'theme' was removed in protocol 1.0.") { SurfaceId = surfaceId, Path = "/theme" };
        }

        string? catalogId = null;
        if (body.ContainsKey("catalogId"))
        {
            catalogId = A2UIValues.AsString(body["catalogId"]) is { Length: > 0 } id
                ? id
                : throw new A2UIValidationException("catalogId must be a non-empty string.") { SurfaceId = surfaceId };
            if (!Catalogs.TryGet(catalogId, out Catalog? named) || named.ProtocolVersion != A2UIProtocol.Version)
            {
                throw new A2UICatalogException($"Catalog not found: {catalogId}") { SurfaceId = surfaceId };
            }
        }

        JsonObject? metadata = ReadMetadata(body["metadata"], surfaceId, "createSurface.metadata");
        if (body["dataModel"] is { } dm && dm is not JsonObject)
        {
            throw new A2UIValidationException("dataModel must be an object.") { SurfaceId = surfaceId };
        }

        lock (_reservedSurfaceIds)
        {
            if (!_reservedSurfaceIds.Add(surfaceId))
            {
                throw new A2UIException(A2UIErrorCodes.IntegrityError, $"Surface '{surfaceId}' already exists.") { SurfaceId = surfaceId };
            }
        }

        Surface surface = new(
            this,
            surfaceId,
            catalogId,
            body["sendDataModel"] is JsonValue sdm && sdm.GetValueKind() == JsonValueKind.True,
            metadata);

        try
        {
            // The surface is built and validated first, and only published once it is whole,
            // so a rejected createSurface leaves no half-built surface behind.
            using (surface.BatchChanges())
            {
                if (body["dataModel"] is JsonObject dataModel)
                {
                    surface.DataModel.Set("/", dataModel);
                }

                if (body["components"] is { } components)
                {
                    ApplyComponents(surface, components, validation);
                }
            }
        }
        catch
        {
            lock (_reservedSurfaceIds)
            {
                _reservedSurfaceIds.Remove(surfaceId);
            }

            throw;
        }

        _surfaces[surfaceId] = surface;
        SurfaceCreated?.Invoke(surface);
    }

    /// <summary>
    /// Handles <c>updateComponents</c>.
    /// </summary>
    /// <exception cref="A2UIException">The surface does not exist or the message is invalid.</exception>
    /// <param name="body">The <c>updateComponents</c> body.</param>
    /// <param name="validation">The completeness rules for this message, or null for a streaming renderer.</param>
    private void UpdateComponents(JsonObject body, A2UIValidationConfig? validation)
    {
        string surfaceId = ReadSurfaceId(body);
        Surface surface = GetLiveSurface(surfaceId);
        if (body["components"] is not { } components)
        {
            throw new A2UIValidationException("updateComponents needs a components list.") { SurfaceId = surfaceId };
        }

        ApplyComponents(surface, components, validation);
    }

    /// <summary>
    /// Parses, validates and applies a list of components. Components that fail to parse are reported one by one; rules over the whole tree throw.
    /// </summary>
    /// <exception cref="A2UIException">The list is empty or not an array, or a tree rule (composition, cycles, depth, completeness) fails under validation.</exception>
    /// <param name="surface">The surface to update.</param>
    /// <param name="components">The components array.</param>
    /// <param name="validation">The completeness rules for this message, or null for a streaming renderer.</param>
    private void ApplyComponents(Surface surface, JsonNode components, A2UIValidationConfig? validation)
    {
        if (components is not JsonArray list || list.Count == 0)
        {
            throw new A2UIValidationException("components must be a non-empty array.") { SurfaceId = surface.SurfaceId };
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ComponentInstance> parsed = new();
        bool parseFailed = false;
        Dictionary<string, int> batchIndex = new(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            string path = $"/components/{i}";
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
                parsed.Add(ComponentParser.Parse(raw, surface, validation));
            }
            catch (A2UIException ex)
            {
                parseFailed = true;
                ReportValidation(surface.SurfaceId, path + (ex.Path ?? string.Empty), ex.Message, ex.Code);
            }
        }

        Dictionary<string, ComponentInstance> candidate = surface.SnapshotComponents();
        foreach (ComponentInstance component in parsed)
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
        if (validation is { } rules && !parseFailed)
        {
            CompletenessCheck(surface.SurfaceId, candidate, rules);
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
        foreach (ComponentInstance component in components.Values)
        {
            foreach ((string childId, string? pointer) in component.ChildReferences())
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

        Dictionary<string, int> state = new(StringComparer.Ordinal); // 1 = visiting, 2 = done
        Stack<(string Id, int Depth, IEnumerator<string> Children)> stack = new();
        state["root"] = 1;
        stack.Push(("root", 0, components["root"].StaticChildIds().GetEnumerator()));
        while (stack.Count > 0)
        {
            (string? id, int depth, IEnumerator<string>? children) = stack.Peek();
            if (!children.MoveNext())
            {
                state[id] = 2;
                stack.Pop();
                continue;
            }

            string childId = children.Current;
            if (!components.TryGetValue(childId, out ComponentInstance? child))
            {
                continue; // Progressive rendering: the child may arrive later.
            }

            if (state.TryGetValue(childId, out int s) && s == 1)
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
            foreach (ComponentInstance component in components.Values)
            {
                foreach ((string childId, string? pointer) in component.ChildReferences())
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
            HashSet<string> reached = new(StringComparer.Ordinal) { "root" };
            Queue<string> queue = new();
            queue.Enqueue("root");
            while (queue.Count > 0)
            {
                foreach (string childId in components[queue.Dequeue()].StaticChildIds())
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

    /// <summary>
    /// Formats <c>/slots/1/child</c> as <c>slots[1].child</c>.
    /// </summary>
    private static string FieldName(string pointer)
    {
        StringBuilder builder = new();
        foreach (string segment in JsonPointer.Parse(pointer))
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

        HashSet<string> visited = new(StringComparer.Ordinal) { "root" };
        Queue<string> queue = new();
        queue.Enqueue("root");
        while (queue.Count > 0)
        {
            ComponentInstance component = components[queue.Dequeue()];
            foreach ((string key, JsonNode? value) in component.Properties)
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

            foreach (string childId in component.StaticChildIds(includeTemplates: false))
            {
                if (components.ContainsKey(childId) && visited.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
    }

    /// <summary>
    /// Whether a value contains an <c>@index</c> call anywhere, up to the nesting limit.
    /// </summary>
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
        Dictionary<string, List<ComponentInstance>> parents = new(StringComparer.Ordinal);
        foreach (ComponentInstance parent in components.Values)
        {
            foreach (string childId in parent.StaticChildIds())
            {
                if (!parents.TryGetValue(childId, out List<ComponentInstance>? list))
                {
                    parents[childId] = list = [];
                }

                list.Add(parent);
            }
        }

        string Pointer(string id, string suffix) =>
            "/components/" + (batchIndex.TryGetValue(id, out int index) ? index.ToString(CultureInfo.InvariantCulture) : JsonPointer.Escape(id)) + suffix;

        static string Format(IReadOnlyList<string> names) => "[" + string.Join(", ", names.Select(n => $"'{n}'")) + "]";

        foreach (ComponentInstance component in components.Values)
        {
            if (component.Definition.AllowedParents is { Count: > 0 } allowedParents)
            {
                List<(string Id, string Type)> toCheck = new();
                if (component.Id == "root")
                {
                    toCheck.Add((Composition.SurfaceType, Composition.SurfaceType));
                }

                toCheck.AddRange((parents.GetValueOrDefault(component.Id) ?? []).Select(p => (p.Id, p.Type)));
                foreach ((string parentId, string? parentType) in toCheck)
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
                foreach ((string childId, string? pointer) in component.ChildReferences())
                {
                    if (components.TryGetValue(childId, out ComponentInstance? child) && !allowedChildren.Contains(child.Type))
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

    /// <summary>
    /// Handles <c>updateDataModel</c>: writes or deletes the value at an absolute path.
    /// </summary>
    /// <exception cref="A2UIException">The surface does not exist, or the path or write is invalid; the data model is unchanged.</exception>
    private void UpdateDataModel(JsonObject body)
    {
        string surfaceId = ReadSurfaceId(body);
        Surface surface = GetLiveSurface(surfaceId);
        if (!body.TryGetPropertyValue("value", out JsonNode? value))
        {
            throw new A2UIValidationException("updateDataModel needs a 'value' (use null to delete).") { SurfaceId = surfaceId };
        }

        string path = body.ContainsKey("path")
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

    /// <summary>
    /// A null written to a list element keeps the list's length (the element becomes null).
    /// </summary>
    private static bool IsListElement(DataModel model, string path)
    {
        IReadOnlyList<string> segments = JsonPointer.Parse(path);
        if (segments.Count == 0)
        {
            return false;
        }

        return model.TryGet(JsonPointer.Format(segments.Take(segments.Count - 1)), out JsonNode? parent) && parent is JsonArray;
    }

    /// <summary>
    /// Handles <c>deleteSurface</c>: removes the surface, cancels its agent calls and detaches its observers. Deleting a missing surface does nothing.
    /// </summary>
    private void DeleteSurface(JsonObject body)
    {
        string surfaceId = ReadSurfaceId(body);
        if (!_surfaces.TryRemove(surfaceId, out Surface? surface))
        {
            return;
        }

        lock (_reservedSurfaceIds)
        {
            _reservedSurfaceIds.Remove(surfaceId);
        }

        surface.IsDeleted = true;
        surface.AgentCalls.CancelAll("The surface was deleted.");
        surface.LocalCalls.Clear();
        surface.DataModel.DisposeObservers();
        SurfaceDeleted?.Invoke(surface);
    }

    /// <summary>
    /// Handles <c>agentFunctionResponse</c> by settling the matching pending call.
    /// </summary>
    /// <exception cref="A2UIException">The response is malformed.</exception>
    private void HandleAgentFunctionResponse(JsonObject body)
    {
        string id = A2UIValues.AsString(body["functionCallId"])
                 ?? throw new A2UIValidationException("agentFunctionResponse needs a functionCallId.");
        if (body.ContainsKey("value") == body.ContainsKey("error"))
        {
            throw new A2UIValidationException("agentFunctionResponse must have exactly one of 'value' or 'error'.") { FunctionCallId = id };
        }

        if (body.ContainsKey("error")
            && (body["error"] is not JsonObject error || A2UIValues.AsString(error["code"]) is null || A2UIValues.AsString(error["message"]) is null))
        {
            // Checked before the call is looked up, so a malformed error never settles a call as a success.
            throw new A2UIValidationException("agentFunctionResponse 'error' must be an object with string 'code' and 'message'.")
            {
                FunctionCallId = id,
                Path = "/error",
            };
        }

        foreach (Surface surface in _surfaces.Values)
        {
            if (surface.AgentCalls.TryComplete(id, body))
            {
                return;
            }
        }

        // Unknown, late or duplicate responses are ignored.
    }

    /// <summary>
    /// Rejects a malformed <c>callRendererFunction</c> envelope before anything runs.
    /// </summary>
    private static void ValidateCallRendererFunction(JsonObject body)
    {
        string functionCallId = A2UIValues.AsString(body["functionCallId"]) is { Length: > 0 } id
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

    /// <summary>
    /// Handles <c>callRendererFunction</c>: runs a renderer function for the agent and sends back the value or the error.
    /// </summary>
    /// <param name="body">The <c>callRendererFunction</c> body.</param>
    /// <param name="userActivated">Whether the message was processed inside a user gesture.</param>
    private async Task HandleCallRendererFunctionAsync(JsonObject body, bool userActivated)
    {
        string? functionCallId = A2UIValues.AsString(body["functionCallId"]);
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
                string? surfaceId = A2UIValues.AsString(body["surfaceId"]);
                if (surfaceId is null || !_surfaces.TryGetValue(surfaceId, out Surface? surface))
                {
                    throw new A2UIException(A2UIErrorCodes.InvalidFunctionCall, $"Surface '{surfaceId}' does not exist.");
                }

                context = surface.RootContext;
            }
            else
            {
                context = EvaluationContext.Detached();
            }

            JsonNode? value = await Evaluator.EvaluateAsync(call, context, new EvaluationScope(EvaluationMode.AgentCall) { UserActivationOverride = userActivated }).ConfigureAwait(false);
            await SendOutboundAsync(new JsonObject
            {
                ["version"] = A2UIProtocol.Version,
                ["rendererFunctionResponse"] = new JsonObject { ["functionCallId"] = functionCallId, ["value"] = value },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string code = ex is A2UIException { Code: A2UIErrorCodes.InvalidFunctionCall } ? A2UIErrorCodes.InvalidFunctionCall : A2UIErrorCodes.ExecutionError;
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

    /// <summary>
    /// Runs an action for a component after a user gesture. Failures are reported, never thrown.
    /// </summary>
    /// <returns>True when the action ran: its event was sent or its function call completed. False when the component has no action or the action failed; the failure was reported.</returns>
    internal async Task<bool> DispatchActionAsync(
        Surface surface,
        ComponentInstance component,
        JsonNode? action,
        EvaluationContext context,
        JsonObject? metadata,
        CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return false;
        }

        try
        {
            await DispatchActionCoreAsync(surface, component.Id, action, context, metadata, userActivated: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (A2UIException ex)
        {
            ReportActionError(surface, ex);
            return false;
        }
    }

    /// <summary>
    /// Runs an action: sends its <c>event</c> to the agent, or runs its local <c>functionCall</c>.
    /// </summary>
    /// <param name="surface">The surface the action belongs to.</param>
    /// <param name="sourceComponentId">The component the user acted on.</param>
    /// <param name="action">The component's <c>action</c> property.</param>
    /// <param name="context">The data scope the action resolves against.</param>
    /// <param name="metadata">The host's <c>metadata</c> for the dispatched event, or null.</param>
    /// <param name="userActivated">Whether a user gesture started the action.</param>
    /// <param name="cancellationToken">Cancels agent calls the action makes.</param>
    /// <returns>The result of a <c>functionCall</c> action; null for an event or a missing action.</returns>
    /// <exception cref="A2UIException">
    /// The action is malformed (<see cref="A2UIErrorCodes.InvalidAction"/>), its metadata is invalid, or a call in it failed.
    /// Nothing is sent to the agent after a failure.
    /// </exception>
    internal async Task<JsonNode?> DispatchActionCoreAsync(
        Surface surface,
        string sourceComponentId,
        JsonNode? action,
        EvaluationContext context,
        JsonObject? metadata,
        bool userActivated,
        CancellationToken cancellationToken)
    {
        if (action is not JsonObject actionObject)
        {
            return null;
        }

        EvaluationScope scope = new(EvaluationMode.Action) { CancellationToken = cancellationToken, UserActivationOverride = userActivated };
        if (actionObject["functionCall"] is JsonObject functionCall)
        {
            return await Evaluator.EvaluateAsync(functionCall, context, scope).ConfigureAwait(false);
        }

        if (actionObject["event"] is not JsonObject ev)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidAction, "An action needs an 'event' or a 'functionCall'.");
        }

        if (A2UIValues.AsString(ev["name"]) is not { Length: > 0 } name)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidAction, "An action event needs a non-empty string 'name'.");
        }

        JsonObject? checkedMetadata = ReadMetadata(metadata, surface.SurfaceId, "Action metadata");
        JsonObject eventContext = new();
        if (ev["context"] is JsonObject rawContext)
        {
            // Entries resolve in order; the first failure stops the action.
            foreach ((string key, JsonNode? raw) in rawContext)
            {
                try
                {
                    eventContext[A2UIEvaluator.UnescapeKey(key)] = await Evaluator.EvaluateAsync(raw, context, scope).ConfigureAwait(false);
                }
                catch (A2UIException ex) when (ex.Code != A2UIErrorCodes.Cancelled)
                {
                    throw new A2UIExpressionException($"Action event context entry '{key}' is invalid: {ex.Message}") { FunctionCallId = ex.FunctionCallId };
                }
            }
        }
        else if (ev["context"] is not null)
        {
            throw new A2UIException(A2UIErrorCodes.InvalidAction, "An action event's context must be an object.");
        }

        string? userMessage = null;
        if (ev["userMessage"] is { } rawMessage)
        {
            JsonNode? resolved = await Evaluator.EvaluateAsync(rawMessage, context, scope).ConfigureAwait(false);
            if (resolved is not null && !(resolved is JsonValue sv && sv.GetValueKind() == JsonValueKind.String))
            {
                throw new A2UIException(A2UIErrorCodes.InvalidAction, "An action event's userMessage must resolve to a string.");
            }

            userMessage = A2UIValues.AsString(resolved);
        }

        JsonObject body = new()
        {
            ["name"] = name,
            ["surfaceId"] = surface.SurfaceId,
            ["sourceComponentId"] = sourceComponentId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["context"] = eventContext,
        };
        if (userMessage is not null)
        {
            body["userMessage"] = userMessage;
        }

        if (checkedMetadata is not null)
        {
            body["metadata"] = checkedMetadata;
        }

        await SendOutboundAsync(new JsonObject { ["version"] = A2UIProtocol.Version, ["action"] = body }).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Reports a failed action on its surface. Cancellation is not reported, and the error keeps its code and function call id.
    /// </summary>
    internal void ReportActionError(Surface surface, A2UIException error)
    {
        if (error.Code == A2UIErrorCodes.Cancelled)
        {
            return;
        }

        ReportError(new A2UIException(error.Code, error.Message) { FunctionCallId = error.FunctionCallId, Path = error.Path }, surface.SurfaceId);
    }

    /// <summary>
    /// Reports a validation problem, keeping the codes the spec names and mapping the rest to <see cref="A2UIErrorCodes.ValidationFailed"/>.
    /// </summary>
    internal void ReportValidation(string surfaceId, string path, string message, string code = A2UIErrorCodes.ValidationFailed)
    {
        string wireCode = code is A2UIErrorCodes.UnallowedChild or A2UIErrorCodes.UnallowedParent or A2UIErrorCodes.CatalogError
            or A2UIErrorCodes.IntegrityError or A2UIErrorCodes.RecursionLimitExceeded
            ? code
            : A2UIErrorCodes.ValidationFailed;
        ReportError(new A2UIException(wireCode, message) { SurfaceId = surfaceId, Path = path }, surfaceId);
    }

    /// <summary>
    /// Reports an error locally and, when configured, to the agent.
    /// </summary>
    public void ReportError(A2UIException error, string? surfaceId)
    {
        if (error.Code == A2UIErrorCodes.Cancelled)
        {
            return; // A cancelled call was cancelled on purpose (surface deleted or calls cancelled).
        }

        string? functionCallId = error.FunctionCallId;
        surfaceId = functionCallId is null ? surfaceId ?? error.SurfaceId : null;
        A2UIErrorReport report = new(error.Code, error.Message, surfaceId, functionCallId, error.Path);
        ErrorReported?.Invoke(report);

        if (!Options.SendErrorsToAgent || !HasOutbound || (surfaceId is null && functionCallId is null))
        {
            return;
        }

        JsonObject body = new() { ["code"] = report.Code, ["message"] = report.Message };
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

    /// <summary>
    /// Sends a message to the agent in the background; a failed send is reported through <see cref="ErrorReported"/>.
    /// </summary>
    internal void SendOutbound(JsonObject message) => Track(SendOutboundAsync(message));

    /// <summary>
    /// Sends a message to the agent with the renderer's data model attached when surfaces ask for it. Does nothing without a transport.
    /// </summary>
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

    /// <summary>
    /// Keeps a send that is still running, so it can be awaited later.
    /// </summary>
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

    /// <summary>
    /// Reads and checks a <c>metadata</c> object: only <c>extensions</c> is allowed, and its keys must be UAX #31 identifiers.
    /// </summary>
    /// <returns>The metadata, or null when there is none.</returns>
    /// <exception cref="A2UIValidationException">The metadata is not an object or has a key that is not allowed.</exception>
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

        foreach ((string key, JsonNode? value) in metadata)
        {
            if (key != "extensions")
            {
                throw new A2UIValidationException($"{where} may only hold 'extensions', not '{key}'.") { SurfaceId = surfaceId };
            }

            if (value is not JsonObject extensions)
            {
                throw new A2UIValidationException($"{where}.extensions must be an object.") { SurfaceId = surfaceId };
            }

            foreach ((string extensionKey, JsonNode? _) in extensions)
            {
                if (!Uax31.IsIdentifier(extensionKey))
                {
                    throw new A2UIValidationException($"Extension key '{extensionKey}' is not a UAX #31 identifier.") { SurfaceId = surfaceId };
                }
            }
        }

        return (JsonObject)metadata.DeepClone();
    }

    /// <summary>
    /// The settings one entry point processes a message with. They are passed down explicitly, so a message
    /// processed reentrantly (from an event handler) or concurrently never inherits another call's settings.
    /// </summary>
    /// <param name="Validation">The completeness rules, or null for a streaming renderer.</param>
    /// <param name="UserActivated">Whether the message was processed inside a user gesture.</param>
    private readonly record struct ProcessingState(A2UIValidationConfig? Validation, bool UserActivated);
}
