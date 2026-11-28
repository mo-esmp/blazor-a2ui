using BlazorA2UI.BlazorWasmSample;
using BlazorA2UI.BlazorWasmSample.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The agent's endpoint comes from wwwroot/appsettings.json, never from a message.
SampleRendererOptions options = SampleRendererOptions.Load(builder.Configuration);

// The agent is on another origin, so the client gets an HttpClient without a base address and sends to the endpoint's
// absolute URL. The browser makes the request, so the agent's CORS policy must allow this app's origin.
builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddSampleRenderer(options);

await builder.Build().RunAsync();
