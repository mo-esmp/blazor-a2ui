namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Runs one turn of the agent: it receives the user's input and sends the agent's messages to the renderer.
/// </summary>
/// <param name="turn">The turn: the input, and the methods that send the agent's answer.</param>
/// <param name="cancellationToken">Cancelled when the client disconnects or the server shuts down.</param>
public delegate Task A2UIAgentHandler(A2UIAgentTurn turn, CancellationToken cancellationToken);
