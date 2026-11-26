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
    /// Serializes the agent's sends, so each message is checked, written and kept in the mirror as one step.
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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
    /// <param name="owner">The key of the user the conversation belongs to, or null.</param>
    /// <param name="options">The endpoint's options.</param>
    public A2UIConversation(string id, string? owner, A2UIAgentEndpointOptions options)
    {
        Id = id;
        Owner = owner;
        LogId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)).AsSpan(0, 6));
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
    /// Gets the key of the user the conversation belongs to, or null when it belongs to no one.
    /// </summary>
    public string? Owner { get; }

    /// <summary>
    /// Gets a fingerprint of the id for logs. The id itself resumes the conversation, so it never goes to a log.
    /// </summary>
    public string LogId { get; }

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
    /// Checks a message the way the renderer will, writes it, and keeps it in the mirror of the renderer's surfaces only
    /// when both succeed. Sends are serialized, so the mirror applies messages in the order the client receives them.
    /// </summary>
    /// <returns>The first problem the renderer would report, in which case nothing is written; or null once it is written.</returns>
    /// <param name="message">The message. It is not changed.</param>
    /// <param name="writer">Writes the response.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task<A2UIErrorReport?> SendAsync(JsonObject message, A2UIStreamWriter writer, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            A2UIMessageValidator.Checkpoint checkpoint;
            lock (_validatorGate)
            {
                A2UIValidationResult result = _validator.ValidateAtomically(message.DeepClone().AsObject(), out checkpoint);
                if (!result.IsValid)
                {
                    return result.Errors[0];
                }
            }

            try
            {
                await writer.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The client did not get the message whole, so the mirror must not keep it.
                lock (_validatorGate)
                {
                    _validator.Restore(checkpoint);
                }

                throw;
            }

            return null;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Writes a renderer function call once the surface it runs against is in the mirror of the renderer's surfaces.
    /// A call that ends, for example by timing out, while it waits for its turn to be written is not written.
    /// The call itself was checked against the catalogs when it was made; the mirror cannot check it further, because
    /// the renderer's functions run only on the renderer. Sends are serialized with <see cref="SendAsync"/>.
    /// </summary>
    /// <returns>
    /// The problem the renderer would report, in which case nothing is written; or null once it is written or once the
    /// call has ended without it.
    /// </returns>
    /// <param name="call">The call.</param>
    /// <param name="surfaceId">The surface the call runs against, or null.</param>
    /// <param name="writer">Writes the response.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task<A2UIErrorReport?> SendCallAsync(A2UIRendererFunctionCall call, string? surfaceId, A2UIStreamWriter writer, CancellationToken cancellationToken)
    {
        using (CancellationTokenSource ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Stop waiting for the gate as soon as the call ends, so an abandoned call is never written. The state is
            // always the source passed here.
            _ = call.Result.ContinueWith(static (_, state) => CancelQuietly((CancellationTokenSource)state!), ended, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await _sendGate.WaitAsync(ended.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        try
        {
            if (call.Result.IsCompleted)
            {
                return null;
            }

            if (surfaceId is not null)
            {
                bool exists;
                lock (_validatorGate)
                {
                    exists = _validator.Surfaces.ContainsKey(surfaceId);
                }

                if (!exists)
                {
                    return new A2UIErrorReport(A2UIErrorCodes.InvalidFunctionCall, $"Surface '{surfaceId}' does not exist.", null, call.FunctionCallId, null);
                }
            }

            await writer.WriteMessageAsync(call.Message, cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Cancels a source that may already be disposed.
    /// </summary>
    /// <param name="source">The source.</param>
    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait already ended.
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
