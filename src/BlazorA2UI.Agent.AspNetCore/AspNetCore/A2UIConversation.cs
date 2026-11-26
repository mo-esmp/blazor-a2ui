using BlazorA2UI.Processing;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// What the endpoint keeps between requests of one conversation: a mirror of the renderer's surfaces to check the
/// agent's messages against, and the renderer function calls still waiting for their responses.
/// </summary>
internal sealed class A2UIConversation : IDisposable
{
    /// <summary>
    /// Guards <see cref="_validator"/>, which is not safe for concurrent use.
    /// </summary>
    private readonly Lock _validatorGate = new();

    /// <summary>
    /// Guards <see cref="_activeRequests"/>, <see cref="_lastUsed"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly Lock _stateGate = new();

    /// <summary>
    /// Mirrors the renderer's surfaces, to check the agent's messages the way the renderer will.
    /// </summary>
    private readonly A2UIMessageValidator _validator;

    /// <summary>
    /// Allows one agent turn at a time.
    /// </summary>
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>
    /// How many requests are using the conversation.
    /// </summary>
    private int _activeRequests;

    /// <summary>
    /// When the last request ended, or the conversation started.
    /// </summary>
    private DateTimeOffset _lastUsed;

    /// <summary>
    /// Whether the conversation was disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates a conversation.
    /// </summary>
    /// <param name="id">The conversation's id.</param>
    /// <param name="options">The endpoint's options.</param>
    public A2UIConversation(string id, A2UIAgentEndpointOptions options)
    {
        Id = id;
        _validator = new A2UIMessageValidator(options.Catalogs, options.Validation);
        RendererCalls = new A2UIRendererFunctionCalls(new A2UIRendererFunctionCallsOptions
        {
            Catalogs = options.Catalogs,
            AgentFunctions = options.AgentFunctions,
            DefaultTimeout = options.RendererFunctionTimeout,
            MaxPending = options.MaxPendingRendererCalls,
            TimeProvider = options.TimeProvider,
        });
        _lastUsed = options.TimeProvider.GetUtcNow();
    }

    /// <summary>
    /// Gets the conversation's id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the renderer function calls the agent made in this conversation.
    /// </summary>
    public A2UIRendererFunctionCalls RendererCalls { get; }

    /// <summary>
    /// Marks the conversation as used by a request, unless it was disposed.
    /// </summary>
    /// <returns>False when the conversation was disposed and may not be used.</returns>
    public bool TryAcquire()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _activeRequests++;
            return true;
        }
    }

    /// <summary>
    /// Marks a request as done with the conversation.
    /// </summary>
    /// <param name="now">The time the request ended.</param>
    public void Release(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            _activeRequests--;
            _lastUsed = now;
        }
    }

    /// <summary>
    /// Disposes the conversation if no request is using it and it has been idle for longer than the timeout.
    /// </summary>
    /// <returns>True when the conversation was disposed.</returns>
    /// <param name="now">The current time.</param>
    /// <param name="idleTimeout">How long an idle conversation is kept.</param>
    public bool TryExpire(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_stateGate)
        {
            if (_disposed || _activeRequests > 0 || now - _lastUsed < idleTimeout)
            {
                return false;
            }

            _disposed = true;
        }

        DisposeResources();
        return true;
    }

    /// <summary>
    /// Starts an agent turn, unless one is already running.
    /// </summary>
    /// <returns>False when another turn is running.</returns>
    public bool TryStartTurn() => _turnGate.Wait(0);

    /// <summary>
    /// Ends the running agent turn.
    /// </summary>
    public void EndTurn() => _turnGate.Release();

    /// <summary>
    /// Checks a message the agent is about to send against the mirror of the renderer's surfaces, and applies it to
    /// the mirror when it passes.
    /// </summary>
    /// <returns>The first problem the renderer would report, or null when it would accept the message.</returns>
    /// <param name="message">The message. It is not changed.</param>
    public A2UIErrorReport? Check(JsonObject message)
    {
        lock (_validatorGate)
        {
            A2UIValidationResult result = _validator.Validate(message.DeepClone());
            return result.IsValid ? null : result.Errors[0];
        }
    }

    /// <summary>
    /// Disposes the conversation, failing its pending renderer function calls.
    /// </summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        DisposeResources();
    }

    /// <summary>
    /// Releases what the conversation holds.
    /// </summary>
    private void DisposeResources()
    {
        RendererCalls.Dispose();
        lock (_validatorGate)
        {
            _validator.Dispose();
        }
    }
}
