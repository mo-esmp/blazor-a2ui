namespace BlazorA2UI.SampleAgent;

/// <summary>
/// The sample's settings, read from the <c>SampleAgent</c> section of the configuration: <c>appsettings.json</c>, user
/// secrets in Development, and environment variables such as <c>SampleAgent__Model__ApiKey</c>.
/// </summary>
internal sealed class SampleAgentOptions
{
    /// <summary>
    /// The configuration section that holds the settings.
    /// </summary>
    public const string Section = "SampleAgent";

    /// <summary>
    /// Gets or sets the origins of the renderer apps allowed to call the agent from a browser, such as
    /// <c>https://localhost:7090</c>.
    /// </summary>
    public string[] RendererOrigins { get; set; } = [];

    /// <summary>
    /// Gets or sets the model to use. Without an API key the sample answers from its offline script.
    /// </summary>
    public SampleModelOptions Model { get; set; } = new();

    /// <summary>
    /// Checks the settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">An origin is not a bare http or https origin, or the model has no name.</exception>
    internal void Validate()
    {
        foreach (string origin in RendererOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath != "/"
                || origin.EndsWith('/')
                || uri.Query.Length > 0
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException($"'{origin}' in {Section}:RendererOrigins is not an origin such as https://localhost:7090.");
            }
        }

        if (Model.Endpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException($"{Section}:Model:Endpoint must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(Model.Name))
        {
            throw new InvalidOperationException($"{Section}:Model:Name must name a model.");
        }
    }
}

/// <summary>
/// The model the sample talks to, through an OpenAI-compatible API.
/// </summary>
internal sealed class SampleModelOptions
{
    /// <summary>
    /// Gets or sets the API's base URL, or null for OpenAI. Any OpenAI-compatible API works, such as a local Ollama at
    /// <c>http://localhost:11434/v1</c>.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the model's name.
    /// </summary>
    public string Name { get; set; } = "gpt-4.1-mini";

    /// <summary>
    /// Gets or sets the API key, or null to run offline from the script. Keep it in user secrets or an environment
    /// variable, never in <c>appsettings.json</c>.
    /// </summary>
    public string? ApiKey { get; set; }
}
