using BlazorA2UI.BlazorServerSample.Components;

namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// Builds the Blazor Server sample's web app.
/// </summary>
internal static class BlazorServerSampleApp
{
    /// <summary>
    /// Builds the app: interactive server rendering, the renderer's services and the pretend broken agent.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    /// <returns>The app, ready to run.</returns>
    /// <param name="args">The command line.</param>
    /// <param name="configureServices">Changes the services after the sample's own, or null.</param>
    public static WebApplication Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        SampleRendererOptions options = builder.Configuration.GetSection(SampleRendererOptions.Section).Get<SampleRendererOptions>() ?? new SampleRendererOptions();
        options.Validate();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSampleRenderer(options);
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/not-found", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
