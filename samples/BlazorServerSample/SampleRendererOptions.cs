namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// The sample's settings, read from the <c>BlazorServerSample</c> section of the configuration.
/// </summary>
internal sealed class SampleRendererOptions
{
    /// <summary>
    /// The configuration section that holds the settings.
    /// </summary>
    public const string Section = "BlazorServerSample";

    /// <summary>
    /// Gets or sets the agent's endpoint. It comes from the app's settings, never from a message.
    /// </summary>
    public Uri AgentEndpoint { get; set; } = new("http://localhost:5080/a2ui");

    /// <summary>
    /// Gets or sets the URL prefixes images may load from. Any other image renders as a blocked placeholder.
    /// </summary>
    public string[] MediaPrefixes { get; set; } = [];

    /// <summary>
    /// Checks the settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent's endpoint is not an absolute http or https URL.</exception>
    internal void Validate()
    {
        if (!AgentEndpoint.IsAbsoluteUri || AgentEndpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{Section}:AgentEndpoint must be an absolute http or https URL.");
        }
    }
}
