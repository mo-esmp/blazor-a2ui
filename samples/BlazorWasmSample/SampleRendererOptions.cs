namespace BlazorA2UI.BlazorWasmSample;

/// <summary>
/// The sample's settings, read from the <c>BlazorWasmSample</c> section of <c>wwwroot/appsettings.json</c>. The browser
/// downloads that file, so it holds nothing secret.
/// </summary>
internal sealed class SampleRendererOptions
{
    /// <summary>
    /// The configuration section that holds the settings.
    /// </summary>
    public const string Section = "BlazorWasmSample";

    /// <summary>
    /// Gets or sets the agent's endpoint. It comes from the app's settings, never from a message.
    /// </summary>
    public Uri AgentEndpoint { get; set; } = new("http://localhost:5080/a2ui");

    /// <summary>
    /// Gets or sets the URL prefixes images may load from. Any other image renders as a blocked placeholder.
    /// </summary>
    public string[] MediaPrefixes { get; set; } = [];

    /// <summary>
    /// Reads the settings from the configuration and checks them. A missing section gives the defaults.
    /// </summary>
    /// <returns>The checked settings.</returns>
    /// <param name="configuration">The app's configuration.</param>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    public static SampleRendererOptions Load(IConfiguration configuration)
    {
        SampleRendererOptions options = configuration.GetSection(Section).Get<SampleRendererOptions>() ?? new SampleRendererOptions();
        options.Validate();
        return options;
    }

    /// <summary>
    /// Checks the settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The agent's endpoint is not an absolute http or https URL, or a media prefix is not one or has user info, a
    /// query or a fragment.
    /// </exception>
    internal void Validate()
    {
        if (!AgentEndpoint.IsAbsoluteUri || AgentEndpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{Section}:AgentEndpoint must be an absolute http or https URL.");
        }

        // The same rules as A2UIMediaOptions.Allow, checked here so the error names the setting.
        foreach (string prefix in MediaPrefixes)
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"'{prefix}' in {Section}:MediaPrefixes is not an absolute http or https URL without user info, query or fragment.");
            }
        }
    }
}
