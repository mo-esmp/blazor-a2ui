using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Agent;

/// <summary>
/// Checks agent-to-renderer messages the way a renderer would, before they are sent. It mirrors the surfaces a renderer
/// holds: each message is checked against the ones before it, and a valid message is applied to the mirror.
/// </summary>
/// <remarks>
/// <para>
/// The checks are the renderer's own: the validator runs every message through a <see cref="MessageProcessor"/> with
/// the same catalogs. A <c>callRendererFunction</c> envelope is checked but its function never runs.
/// </para>
/// <para>
/// Use one validator per renderer session: a chat agent that creates fresh surfaces each turn can use a new one per
/// turn, and an agent that keeps updating the same surfaces keeps one for the conversation. It is not safe for
/// concurrent use.
/// </para>
/// </remarks>
public sealed class A2UIMessageValidator : IDisposable
{
    /// <summary>
    /// The renderer core the messages run through.
    /// </summary>
    private readonly MessageProcessor _processor;

    /// <summary>
    /// The errors of the message being checked.
    /// </summary>
    private readonly List<A2UIErrorReport> _errors = [];

    /// <summary>
    /// Creates a validator for surfaces that use the basic catalog.
    /// </summary>
    public A2UIMessageValidator()
        : this([BasicCatalog.Create()])
    {
    }

    /// <summary>
    /// Creates a validator for surfaces that use the given catalogs.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalogs"/> is null.</exception>
    /// <param name="catalogs">The catalogs the renderer supports.</param>
    /// <param name="validation">
    /// Completeness rules, such as <see cref="A2UIValidationConfig.Strict"/> to require that every update leaves a
    /// complete tree; null applies the streaming rules a renderer uses by default.
    /// </param>
    public A2UIMessageValidator(IEnumerable<Catalog> catalogs, A2UIValidationConfig? validation = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _processor = new MessageProcessor(catalogs, new A2UIProcessorOptions { SendErrorsToAgent = false, Validation = validation })
        {
            RunsRendererFunctions = false,
        };
        _processor.ErrorReported += _errors.Add;
    }

    /// <summary>
    /// Gets the surfaces the mirror holds after the messages checked so far.
    /// </summary>
    public IReadOnlyDictionary<string, Surface> Surfaces => _processor.Surfaces;

    /// <summary>
    /// Checks a message, a JSON array of messages, or a <c>{"messages": [...]}</c> wrapper, and applies what a renderer
    /// would apply. In a batch, the messages before a rejected one stay applied, as they would in a renderer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The validator is disposed.</exception>
    /// <param name="message">The message.</param>
    public A2UIValidationResult Validate(JsonNode? message)
    {
        ObjectDisposedException.ThrowIf(_processor.IsDisposed, this);
        _errors.Clear();
        try
        {
            _processor.ProcessOrThrow(message);
        }
        catch (A2UIException ex)
        {
            _errors.Add(new A2UIErrorReport(ex.Code, ex.Message, ex.SurfaceId, ex.FunctionCallId, ex.Path));
        }

        return _errors.Count == 0 ? A2UIValidationResult.Valid : new A2UIValidationResult([.. _errors]);
    }

    /// <summary>
    /// Checks one line of a JSON Lines stream.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The validator is disposed.</exception>
    /// <param name="line">The line.</param>
    public A2UIValidationResult ValidateLine(string line)
    {
        ObjectDisposedException.ThrowIf(_processor.IsDisposed, this);
        _errors.Clear();
        _processor.ProcessLine(line);
        return _errors.Count == 0 ? A2UIValidationResult.Valid : new A2UIValidationResult([.. _errors]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _processor.ErrorReported -= _errors.Add;
        _processor.Dispose();
    }
}

/// <summary>
/// The outcome of <see cref="A2UIMessageValidator.Validate"/>.
/// </summary>
/// <param name="Errors">The errors a renderer would report, in order; empty when the message is valid.</param>
public sealed record A2UIValidationResult(IReadOnlyList<A2UIErrorReport> Errors)
{
    /// <summary>
    /// Gets the result of a valid message.
    /// </summary>
    public static A2UIValidationResult Valid { get; } = new([]);

    /// <summary>
    /// Gets whether a renderer would accept the message without an error.
    /// </summary>
    public bool IsValid => Errors.Count == 0;
}
