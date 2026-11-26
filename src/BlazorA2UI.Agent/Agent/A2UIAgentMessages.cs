namespace BlazorA2UI.Agent;

/// <summary>
/// The agent-to-renderer messages a model may write.
/// </summary>
[Flags]
public enum A2UIAgentMessages
{
    /// <summary>
    /// No message.
    /// </summary>
    None = 0,

    /// <summary>
    /// <c>createSurface</c>.
    /// </summary>
    CreateSurface = 1,

    /// <summary>
    /// <c>updateComponents</c>.
    /// </summary>
    UpdateComponents = 2,

    /// <summary>
    /// <c>updateDataModel</c>.
    /// </summary>
    UpdateDataModel = 4,

    /// <summary>
    /// <c>deleteSurface</c>.
    /// </summary>
    DeleteSurface = 8,

    /// <summary>
    /// <c>callRendererFunction</c>.
    /// </summary>
    CallRendererFunction = 16,

    /// <summary>
    /// <c>agentFunctionResponse</c>.
    /// </summary>
    AgentFunctionResponse = 32,

    /// <summary>
    /// The four messages that build and change a UI. The default: a model is not invited to write RPC messages, which
    /// the agent's own code sends.
    /// </summary>
    Ui = CreateSurface | UpdateComponents | UpdateDataModel | DeleteSurface,

    /// <summary>
    /// Every agent-to-renderer message.
    /// </summary>
    All = Ui | CallRendererFunction | AgentFunctionResponse,
}

/// <summary>
/// Maps <see cref="A2UIAgentMessages"/> to the names the wire and the schema use.
/// </summary>
internal static class AgentMessageNames
{
    /// <summary>
    /// Each message, its envelope key and its definition in <c>agent_to_renderer.json</c>.
    /// </summary>
    internal static readonly (A2UIAgentMessages Message, string Key, string Definition)[] s_all =
    [
        (A2UIAgentMessages.CreateSurface, "createSurface", "CreateSurfaceMessage"),
        (A2UIAgentMessages.UpdateComponents, "updateComponents", "UpdateComponentsMessage"),
        (A2UIAgentMessages.UpdateDataModel, "updateDataModel", "UpdateDataModelMessage"),
        (A2UIAgentMessages.DeleteSurface, "deleteSurface", "DeleteSurfaceMessage"),
        (A2UIAgentMessages.CallRendererFunction, "callRendererFunction", "CallRendererFunctionMessage"),
        (A2UIAgentMessages.AgentFunctionResponse, "agentFunctionResponse", "AgentFunctionResponseMessage"),
    ];

    /// <summary>
    /// Returns the message an envelope key names, or <see cref="A2UIAgentMessages.None"/> for an unknown key.
    /// </summary>
    /// <param name="key">The envelope key, such as <c>createSurface</c>.</param>
    public static A2UIAgentMessages FromKey(string key)
    {
        foreach ((A2UIAgentMessages message, string name, string _) in s_all)
        {
            if (name == key)
            {
                return message;
            }
        }

        return A2UIAgentMessages.None;
    }
}
