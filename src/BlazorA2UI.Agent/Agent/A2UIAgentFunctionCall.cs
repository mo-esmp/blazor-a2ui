namespace BlazorA2UI.Agent;

/// <summary>
/// One call from the renderer to an agent function, as its handler receives it.
/// </summary>
/// <param name="CatalogId">The catalog the call was resolved to.</param>
/// <param name="Name">The function's name.</param>
/// <param name="SurfaceId">The surface the call was made on. It comes from the renderer, so check it before trusting it.</param>
/// <param name="FunctionCallId">The id the response copies.</param>
/// <param name="Args">
/// The arguments, checked against the function's argument schema with its defaults applied. The handler owns this copy.
/// </param>
public sealed record A2UIAgentFunctionCall(string CatalogId, string Name, string SurfaceId, string FunctionCallId, JsonObject Args);

/// <summary>
/// Runs an agent function for the renderer.
/// </summary>
/// <remarks>
/// Return the value to send back, or null for a JSON null. Throw an <see cref="A2UIException"/> to answer with its code
/// and message. Any other exception is answered with <see cref="A2UIErrorCodes.ExecutionError"/> and a generic message,
/// so internal details never reach the renderer.
/// </remarks>
/// <param name="call">The call, with its checked arguments.</param>
/// <param name="cancellationToken">Cancelled when the call times out or the caller cancels processing.</param>
public delegate ValueTask<JsonNode?> A2UIAgentFunctionHandler(A2UIAgentFunctionCall call, CancellationToken cancellationToken);
