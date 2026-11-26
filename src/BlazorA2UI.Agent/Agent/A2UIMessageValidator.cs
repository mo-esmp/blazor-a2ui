using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Model;
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

    /// <summary>
    /// Checks one message and keeps it in the mirror only when the renderer would accept it whole. A message the
    /// renderer would apply in part while reporting an error, such as an update with one unknown component, is taken
    /// back out, because the caller will not send it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The validator is disposed.</exception>
    /// <returns>The result.</returns>
    /// <param name="message">The message: one envelope, not a batch. It may be changed, so pass a copy.</param>
    /// <param name="checkpoint">Set to the mirror's state before the message, for <see cref="Restore"/>.</param>
    internal A2UIValidationResult ValidateAtomically(JsonObject message, out Checkpoint checkpoint)
    {
        checkpoint = Capture(message);
        A2UIValidationResult result = Validate(message);
        if (!result.IsValid)
        {
            Restore(checkpoint);
        }

        return result;
    }

    /// <summary>
    /// Takes a message that <see cref="ValidateAtomically"/> applied back out of the mirror, for a message that could
    /// not be sent after all. A <c>deleteSurface</c> is the exception: the surface stays deleted, so later messages to
    /// it are refused rather than sent.
    /// </summary>
    /// <param name="checkpoint">The state before the message.</param>
    internal void Restore(Checkpoint checkpoint)
    {
        if (_processor.IsDisposed || checkpoint.SurfaceId is not { } surfaceId)
        {
            return;
        }

        _processor.Surfaces.TryGetValue(surfaceId, out Surface? current);
        if (checkpoint.Surface is null)
        {
            // The message created the surface, so taking it back deletes the surface.
            if (current is not null)
            {
                _processor.ProcessOrThrow(new JsonObject
                {
                    ["version"] = A2UIProtocol.Version,
                    ["deleteSurface"] = new JsonObject { ["surfaceId"] = surfaceId },
                });
            }

            return;
        }

        if (!ReferenceEquals(current, checkpoint.Surface))
        {
            return;
        }

        if (checkpoint.Components is { } components)
        {
            current.RestoreComponents(components);
        }

        if (checkpoint.DataModel is { } dataModel)
        {
            current.DataModel.Set("/", dataModel);
        }
    }

    /// <summary>
    /// Records what a message can change: the surface it names, and that surface's components or data model.
    /// </summary>
    /// <returns>The checkpoint.</returns>
    /// <param name="message">The message.</param>
    private Checkpoint Capture(JsonObject message)
    {
        string? kind = null;
        string? surfaceId = null;
        foreach ((string key, JsonNode? value) in message)
        {
            if (key != "version" && value is JsonObject body)
            {
                kind = key;
                surfaceId = A2UIValues.AsString(body["surfaceId"]);
                break;
            }
        }

        if (surfaceId is null)
        {
            return new Checkpoint(null, null, null, null);
        }

        if (!_processor.Surfaces.TryGetValue(surfaceId, out Surface? surface))
        {
            return new Checkpoint(surfaceId, null, null, null);
        }

        // Only the part the message can change is copied: an update's components, or a data model write's model.
        return kind switch
        {
            "updateComponents" => new Checkpoint(surfaceId, surface, surface.SnapshotComponents(), null),
            "updateDataModel" => new Checkpoint(surfaceId, surface, null, surface.DataModelSnapshot()),
            _ => new Checkpoint(surfaceId, surface, null, null),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _processor.ErrorReported -= _errors.Add;
        _processor.Dispose();
    }

    /// <summary>
    /// The mirror's state before a message, enough to take the message back out.
    /// </summary>
    /// <param name="SurfaceId">The surface the message names, or null when it names none.</param>
    /// <param name="Surface">The surface before the message, or null when it did not exist.</param>
    /// <param name="Components">The surface's components before an <c>updateComponents</c>, or null.</param>
    /// <param name="DataModel">The surface's data model before an <c>updateDataModel</c>, or null.</param>
    internal sealed record Checkpoint(
        string? SurfaceId,
        Surface? Surface,
        Dictionary<string, ComponentInstance>? Components,
        JsonNode? DataModel);
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
