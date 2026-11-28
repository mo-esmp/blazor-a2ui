# Blazor WebAssembly sample

A standalone Blazor WebAssembly app that talks to the [sample agent](../SampleAgent/README.md)
through `A2UIAgentClient`. It has the same pages as the [Blazor Server sample](../BlazorServerSample/README.md),
but the renderer runs in the browser, so it also shows what changes there:

- **CORS.** The browser calls the agent on another origin. The agent's CORS policy allows this app's
  origins (`SampleAgent:RendererOrigins`, `https://localhost:7090` and `http://localhost:5090` by
  default) and exposes the `A2UI-Conversation` header the client needs.
- **Configuration.** The agent's URL comes from `wwwroot/appsettings.json`, never from a message.
  The browser downloads that file, so it holds nothing secret.
- **Trimming.** The app is published with trimming, and the publish fails on any trim warning from
  the `BlazorA2UI` and `BlazorA2UI.Core` packages or the app.

## Run it

Start the agent, then the app, in two terminals:

```bash
dotnet run --project samples/SampleAgent
dotnet run --project samples/BlazorWasmSample
```

Open `https://localhost:7090` (or `http://localhost:5090`). The agent runs offline without an API
key; see its README to use a model. Browsers treat `localhost` as secure, so the `https` page may
call the agent's `http` endpoint.

To check trimming, publish the app:

```bash
dotnet publish samples/BlazorWasmSample -c Release
```

CI runs the same publish on every pull request.

## Pages

- **Chat** (`/`) sends your text to the agent and shows its answers: the text in the transcript and
  each surface as an `A2UISurface`. Type "Book a table" to get the booking form. The availability
  line calls the agent function `checkAvailability` as you type, and Book makes the agent call the
  renderer function `getTimeZone`. The app answers it from `TimeZoneInfo.Local`, which in
  WebAssembly is the browser's time zone, so no JavaScript is needed. Stop cancels the turn. A
  refused or failed turn shows the client's error. The transcript lasts as long as the conversation,
  so leaving the page and coming back shows it again; its code is shared with the Blazor Server sample.
- **Media** (`/media`) renders a local surface with two images. The one under the app's own
  `/media/` folder is on the allowlist and loads; the other is blocked, and the browser never
  requests it.
- **Errors** (`/errors`) calls a pretend agent whose answers break, once in the middle of a message
  and once by dropping the connection, and shows what the client reports. The pretend agent is the
  client's `HttpClient` handler, so no request leaves the browser. Its code is shared with the
  Blazor Server sample.

## Settings

In `wwwroot/appsettings.json`:

| Setting | Default | What it does |
| --- | --- | --- |
| `BlazorWasmSample:AgentEndpoint` | `http://localhost:5080/a2ui` | The agent's endpoint. It must be an absolute http or https URL. |
| `BlazorWasmSample:MediaPrefixes` | this app's `/media/` URLs | The URL prefixes images may load from. |

If you move the app to another origin, add that origin to the agent's
`SampleAgent:RendererOrigins`, or the browser blocks the agent's answers.

## How it is wired

`Program.cs` registers the `HttpClient` the agent client sends with, and
`SampleRenderer.AddSampleRenderer` registers the rest:

```csharp
SampleRendererOptions options = SampleRendererOptions.Load(builder.Configuration);
builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddSampleRenderer(options);
```

A WebAssembly app has one scope, so the processor and the client last as long as the page.

## Trimming

The packages mark themselves trimmable (`IsTrimmable`), so their own builds run the trim analyzer.
The app's publish then runs the trimmer with its warnings shown: each warning in this repository's
assemblies is listed, and with warnings as errors it fails the publish. Warnings from Blazor's own
assemblies are collapsed and hidden, because Microsoft supports trimming Blazor apps.

## Not for production

The app has no authentication, and the pretend agent behind the Errors page exists only to show
failures.
