# Sample agent

A small table-booking agent built on `BlazorA2UI.Agent.AspNetCore`. It shows the agent-side
packages working together:

- **Prompt and parsing.** `A2UIPromptBuilder` writes the A2UI part of the system prompt, with the
  booking form as its example. The model's answer streams through `SendModelResponseAsync`, which
  parses it with `A2UIResponseStream` and checks each message before it is sent. If the renderer
  would reject a message, the problem goes back to the model once so it can fix the answer.
- **Model access** through `Microsoft.Extensions.AI`'s `IChatClient`, with any OpenAI-compatible API.
- **Offline mode.** Without an API key, the agent answers from a short script. The script's answers
  go through the same parser and checks, so the sample and its tests need no key or network.
- **An interactive form.** The form's availability line calls the agent function
  `checkAvailability` as the user types. The Book button's `book` action is handled in code from
  start to finish: the agent checks the request, calls the renderer function `getTimeZone`, and
  replaces the form with a confirmation.
- **CORS** allows only the renderer apps' origins.

## Run it

```bash
dotnet run --project samples/SampleAgent
```

It listens on `https://localhost:7080` and `http://localhost:5080`, and the endpoint is `/a2ui`.
Try it without a renderer:

```bash
curl -N http://localhost:5080/a2ui -H "Content-Type: application/json" -d '{"text":"Book a table"}'
```

The answer streams as JSON Lines. Add `-H "Accept: text/event-stream"` to get server-sent events,
which also carry the agent's text.

## Use a model

Set an API key and the agent uses the model instead of the script. Keep the key in user secrets
or an environment variable, never in `appsettings.json`:

```bash
dotnet user-secrets --project samples/SampleAgent set "SampleAgent:Model:ApiKey" "<your key>"
dotnet user-secrets --project samples/SampleAgent set "SampleAgent:Model:Name" "gpt-4.1-mini"
```

Or use the environment variables `SampleAgent__Model__ApiKey` and `SampleAgent__Model__Name`.
For another OpenAI-compatible API, set `SampleAgent:Model:Endpoint` as well. For example, a local
Ollama uses `http://localhost:11434/v1`, with any key and one of its models as the name.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `SampleAgent:RendererOrigins` | `https://localhost:7090`, `http://localhost:5090` | The origins of the renderer apps allowed to call the agent from a browser. |
| `SampleAgent:Model:Name` | `gpt-4.1-mini` | The model's name. |
| `SampleAgent:Model:Endpoint` | OpenAI | The base URL of an OpenAI-compatible API. |
| `SampleAgent:Model:ApiKey` | none | The API key. Without one, the agent runs offline. |

## The catalog

`SampleCatalog.cs` declares the two functions. A renderer registers the same catalog next to the
basic catalog and attaches its own `getTimeZone`:

```csharp
Catalog booking = SampleCatalog.Create().WithFunctions(new Dictionary<string, A2UIFunction>
{
    [SampleCatalog.GetTimeZone] = _ => ValueTask.FromResult<JsonNode?>(TimeZoneInfo.Local.Id),
});
```

`checkAvailability` has no implementation on the renderer, so the renderer sends its calls to the
agent. `getTimeZone` is `agentOnly`, so only the agent may call it and UI the model writes never
can.

## Not for production

The endpoint has no authentication, and the restaurant's rules are made up. A real agent puts
`RequireAuthorization()` on the endpoint and books through a real system.
