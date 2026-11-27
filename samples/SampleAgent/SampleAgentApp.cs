using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace BlazorA2UI.SampleAgent;

/// <summary>
/// Builds the sample agent's web app.
/// </summary>
internal static class SampleAgentApp
{
    /// <summary>
    /// The route of the agent's endpoint.
    /// </summary>
    public const string Route = "/a2ui";

    /// <summary>
    /// The name of the CORS policy for the renderer apps.
    /// </summary>
    public const string CorsPolicy = "renderers";

    /// <summary>
    /// Builds the app: the model or the offline script, CORS for the renderer apps, and the agent's endpoint with the
    /// basic catalog, the sample catalog and the <c>checkAvailability</c> agent function.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    /// <returns>The app, ready to run.</returns>
    /// <param name="args">The command line.</param>
    /// <param name="configureServices">Changes the services after the sample's own, such as a test's chat client; or null.</param>
    public static WebApplication Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        SampleAgentOptions options = builder.Configuration.GetSection(SampleAgentOptions.Section).Get<SampleAgentOptions>() ?? new SampleAgentOptions();
        options.Validate();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ConversationHistory>();
        builder.Services.AddSingleton<BookingAgent>();
        builder.Services.AddSingleton(_ => CreateChatClient(options.Model));
        builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(options.RendererOrigins)
            .WithMethods("POST")
            .WithHeaders("Content-Type", A2UIAgentEndpointExtensions.ConversationHeader)
            .WithExposedHeaders(A2UIAgentEndpointExtensions.ConversationHeader)));
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.UseCors();

        Catalog catalog = SampleCatalog.Create();
        TimeProvider time = app.Services.GetRequiredService<TimeProvider>();
        A2UIAgentFunctions functions = new A2UIAgentFunctions([catalog]).Add(
            SampleCatalog.CatalogId,
            SampleCatalog.CheckAvailability,
            (call, _) => ValueTask.FromResult<JsonNode?>(
                BookingRules.CheckAvailability(call.Args["date"], call.Args["guests"], DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))));
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BlazorA2UI.SampleAgent");
        functions.FunctionFailed += (call, ex) => logger.LogError(ex, "The agent function {Name} failed.", call.Name);

        BookingAgent agent = app.Services.GetRequiredService<BookingAgent>();
        app.MapA2UIAgent(Route, agent.RunTurnAsync, endpoint =>
        {
            endpoint.Catalogs.Add(BasicCatalog.Create());
            endpoint.Catalogs.Add(catalog);
            endpoint.AgentFunctions = functions;
        }).RequireCors(CorsPolicy);

        bool offline = app.Services.GetRequiredService<IChatClient>() is ScriptedChatClient;
        app.MapGet("/", () => offline
            ? $"The A2UI sample agent is running offline from its script. Point a renderer at {Route}."
            : $"The A2UI sample agent is running with the model {options.Model.Name}. Point a renderer at {Route}.");
        if (offline)
        {
            logger.LogInformation("No API key is set, so the agent answers from its offline script.");
        }
        else
        {
            logger.LogInformation("The agent uses the model {Model}.", options.Model.Name);
        }

        return app;
    }

    /// <summary>
    /// Creates the chat client: the offline script without an API key, otherwise the model through an
    /// OpenAI-compatible API.
    /// </summary>
    /// <returns>The client.</returns>
    /// <param name="model">The model's settings.</param>
    internal static IChatClient CreateChatClient(SampleModelOptions model)
    {
        if (string.IsNullOrEmpty(model.ApiKey))
        {
            return new ScriptedChatClient();
        }

        OpenAIClientOptions clientOptions = new();
        if (model.Endpoint is not null)
        {
            clientOptions.Endpoint = model.Endpoint;
        }

        return new ChatClient(model.Name, new ApiKeyCredential(model.ApiKey), clientOptions).AsIChatClient();
    }
}
