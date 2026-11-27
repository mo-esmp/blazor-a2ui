# Blazor Server sample

A Blazor Web App with interactive server rendering that talks to the [sample agent](../SampleAgent/README.md)
through `A2UIAgentClient`. The agent is called from the server, so the browser never sees its address and the
agent needs no CORS entry for this app.

## Run it

Start the agent, then the app, in two terminals:

```bash
dotnet run --project samples/SampleAgent
dotnet run --project samples/BlazorServerSample
```

Open `https://localhost:7091` (or `http://localhost:5091`). The agent runs offline without an API key; see its
README to use a model.

## Pages

- **Chat** (`/`) sends your text to the agent and shows its answers: the text in the transcript and each surface
  as an `A2UISurface`. Type "Book a table" to get the booking form. The availability line calls the agent function
  `checkAvailability` as you type, and Book makes the agent call the renderer function `getTimeZone`, which this
  app answers from the browser's time zone through JavaScript. Stop cancels the turn. A refused or failed turn
  shows the client's error.
- **Media** (`/media`) renders a local surface with two images. The one under the app's own `/media/` folder is on
  the allowlist and loads; the other is blocked, and the browser never requests it.
- **Errors** (`/errors`) calls a pretend agent whose answers break, once in the middle of a message and once by
  dropping the connection, and shows what the client reports. The pretend agent is the client's `HttpClient`
  handler, so no request leaves the app.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `BlazorServerSample:AgentEndpoint` | `http://localhost:5080/a2ui` | The agent's endpoint. It must be an absolute http or https URL. |
| `BlazorServerSample:MediaPrefixes` | this app's `/media/` URLs | The URL prefixes images may load from. |

## How it is wired

`SampleRenderer.AddSampleRenderer` registers everything in one place:

```csharp
services.AddA2UI(a2ui =>
{
    foreach (string prefix in options.MediaPrefixes)
    {
        a2ui.Media.Allow(prefix, A2UIMediaKind.Image);
    }

    a2ui.Catalogs.Add(SampleCatalog.Create().WithFunctions(...));
});
services.AddHttpClient(HttpClientName);
services.AddA2UIAgentClient(...);
```

`MessageProcessor` and `A2UIAgentClient` are scoped, so each circuit has its own conversation.

## Not for production

The app has no authentication, and the pretend agent behind the Errors page exists only to show failures.
