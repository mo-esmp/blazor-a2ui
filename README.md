# blazor-a2ui

[![CI](https://github.com/mo-esmp/blazor-a2ui/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mo-esmp/blazor-a2ui/actions/workflows/ci.yml)

A Blazor renderer for [A2UI](https://a2ui.org) **v1.0**, the Agent-to-UI protocol: an agent
streams JSON messages that describe a UI, and the renderer draws it with native components,
keeps a data model, and sends the user's actions back.

> **Status: preview.** The protocol core (`BlazorA2UI.Core`) passes Google's conformance
> suites, and the Razor components (`BlazorA2UI`) render the whole basic catalog. The agent side
> (`BlazorA2UI.Agent`) has its builders, validator, LLM prompts and response parsing, and the
> messages that flow back from the renderer, `BlazorA2UI.Agent.AspNetCore` serves an agent
> over HTTP, and `A2UIAgentClient` connects a Blazor app to it. A sample agent runs it all, and
> Blazor Server and WebAssembly samples render it.
> Names may still change before the first release.
>
> | Part | Package | State |
> | --- | --- | --- |
> | Protocol core: messages, data model, bindings, functions, catalogs, validation, agent and renderer function calls | `BlazorA2UI.Core` | Done |
> | Blazor components: the basic catalog as Razor components, with the safety rules below | `BlazorA2UI` | Done |
> | Agent side: component and message builders, a JSONL writer, a validator that mirrors the renderer | `BlazorA2UI.Agent` | Done |
> | Agent side: messages from the renderer, agent functions, renderer function calls | `BlazorA2UI.Agent` | Done |
> | Agent side: LLM prompts and response parsing | `BlazorA2UI.Agent` | Done |
> | Streaming endpoint for ASP.NET Core | `BlazorA2UI.Agent.AspNetCore` | Done |
> | Renderer-side client for the endpoint | `BlazorA2UI` | Done |
> | Sample agent | `samples/SampleAgent` | Done |
> | Blazor Server sample | `samples/BlazorServerSample` | Done |
> | WebAssembly sample | `samples/BlazorWasmSample` | Done |

## Safety rules

A model writes the UI, so everything in a message is treated as untrusted input:

- **Opening an answer never makes the browser fetch anything.** `Image`, `Video` and
  `AudioPlayer` never put a model-supplied URL into the page. The host app resolves media through
  an allowlist it controls; without one, media render as a blocked placeholder.
- **No model value ever reaches an inline `style`.** Layout options (`justify`, `align`, `variant`,
  `fit`, …) are enumerations mapped to CSS classes; anything outside the enumeration is ignored.
- `openUrl` only accepts `http`/`https`, only runs from a user action, and only through a launcher
  the host registers (`IA2UIUrlLauncher`); without one it does nothing.
- Regular expressions from the model use the A2UI regex dialect with its safety checks (no
  backreferences or lookaround, nested-quantifier and overlap checks, length limits) and run with
  a timeout.
- Messages are bounded: nesting depth, component counts, tree depth, pending agent calls and call
  timeouts all have limits.

The core enforces the parts that are protocol-level (URL schemes, user activation, regex safety,
limits); the component library enforces the rendering rules.

To report a vulnerability, or for the full security model, see [SECURITY.md](SECURITY.md).

## Hosting models

The core has no UI dependency, and the components use only standard Blazor features: no
JavaScript of their own (`Modal` moves focus with Blazor's built-in `FocusAsync`) and no inline
styles. So the same packages are meant to work in every Blazor
hosting model:

| Hosting model | Supported |
| --- | --- |
| Blazor Server (interactive server rendering) | Yes |
| Blazor WebAssembly, standalone or hosted | Yes |
| Blazor Web App with `InteractiveAuto` | Yes |
| Static server-side rendering (no interactivity) | Display only: surfaces render, but inputs and actions need an interactive render mode |
| .NET MAUI Blazor Hybrid | Expected to work, not tested yet |

The core alone (`BlazorA2UI.Core`) runs anywhere .NET 10 runs, including a plain server process
that validates or pre-processes agent output.

## Components

The basic catalog from the A2UI v1.0 specification
(`https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json`) has 18 components. The core
parses and validates all of them, and `BlazorA2UI` renders each one.

| Category | Components | Notes |
| --- | --- | --- |
| Display | `Text`, `Icon`, `Divider` | `Text` renders plain text. Markdown, if added, will never load images or links on its own. A named `Icon` renders the class `a2ui-icon-{name}` for your icon set to style; an `svgPath` icon draws itself. |
| Media | `Image`, `Video`, `AudioPlayer` | Rendered only through the host's media allowlist; otherwise a blocked placeholder. |
| Layout | `Row`, `Column`, `List`, `Card`, `Tabs`, `Modal` | `justify`, `align` and the like map to CSS classes. `List` supports templates with `@index`. |
| Input | `Button`, `TextField`, `CheckBox`, `ChoicePicker`, `Slider`, `DateTimeInput` | Two-way bound to the data model, with `@checks` validation messages. |

The catalog's 14 functions are implemented in the core: `required`, `regex`, `length`,
`numeric`, `email`, `formatString`, `formatNumber`, `formatCurrency`, `formatDate`, `pluralize`,
`openUrl`, `and`, `or` and `not`. Any other function a message calls is sent to the agent with
`callAgentFunction`.

Custom catalogs and components are supported by the core: register a `Catalog` with your own
component and function definitions. Map a custom component to your own Razor component, which
inherits `A2UIComponentBase`, with `options.Components.Register<MyChart>("Chart", catalogId)`.
`options.Components.Unregister("Video")` turns a component off; it then renders a placeholder.

### Differences from the specification

- `DateTimeInput`: the catalog defaults both `enableDate` and `enableTime` to false, which would
  leave an input that accepts nothing. When neither is enabled, the renderer shows a date input.

## Getting started in a Blazor app

**1. Add the package:**

```sh
dotnet add package BlazorA2UI
```

**2. Register the services in `Program.cs`:**

```csharp
builder.Services.AddA2UI(options =>
{
    // Media load only from URLs the host allows. Without this, media render as placeholders.
    options.Media.Allow("https://cdn.example.com/");
});

// Optional: let openUrl open links. Without a launcher it does nothing.
builder.Services.AddScoped<IA2UIUrlLauncher, MyUrlLauncher>();
```

**3. Add the stylesheet** to `App.razor` (Blazor Web App) or `wwwroot/index.html` (standalone
WebAssembly):

```html
<link href="_content/BlazorA2UI/a2ui.css" rel="stylesheet" />
```

**4. Render a surface** in any page or component:

```razor
@using BlazorA2UI.Components

<A2UISurface SurfaceId="my-surface" OnAction="HandleAction" />

@code {
    // Called after a component's action ran; the event itself already went to the agent.
    private void HandleAction(A2UIActionEventArgs e) =>
        Console.WriteLine($"{e.ComponentId} on {e.SurfaceId} was used.");
}
```

**5. Connect to the agent.** Register the client with the agent endpoint's URL, which comes from
your app and never from a message:

```csharp
using BlazorA2UI.Client;

builder.Services.AddA2UIAgentClient(options =>
{
    options.Endpoint = new Uri("https://my-agent/a2ui");
    options.Format = A2UIAgentStreamFormat.EventStream;   // also receive the agent's text
});
```

It uses the `HttpClient` registered in the app's services, as a WebAssembly app has. In Blazor
Server, pass one: `AddA2UIAgentClient(configure, provider => provider.GetRequiredService<IHttpClientFactory>().CreateClient("agent"))`.

Then send the user's text from a component:

```razor
@using BlazorA2UI.Client
@inject A2UIAgentClient Agent
@implements IDisposable

<A2UISurface SurfaceId="main" />
<p>@Agent.State @Agent.LastError</p>

@code {
    protected override void OnInitialized() => Agent.StateChanged += Refresh;

    // Call it from your input's submit handler.
    private Task Ask(string text) => Agent.SendTextAsync(text);

    private void Refresh() => InvokeAsync(StateHasChanged);

    public void Dispose() => Agent.StateChanged -= Refresh;
}
```

The client streams the agent's answer into the scoped `MessageProcessor` as it arrives, so
`<A2UISurface>` re-renders as messages land. It sends the user's actions and the renderer's
errors back as the next turn (one at a time; actions that wait together go in one request), and
sends agent function calls and renderer function responses at once, even while a turn streams. It
keeps the conversation's id between requests. When the agent says the conversation expired, the
client forgets the id, deletes every surface (a new conversation knows none of them) and drops the
waiting actions; the next turn opens a new conversation.

- **Text.** Over server-sent events the agent's text arrives through `TextReceived`. It comes
  from the model, so show it as text, never as markup.
- **State.** `State` is `Idle`, `Connecting`, `Streaming`, `Ended`, `Cancelled` or `Failed`;
  `LastError` says why a turn failed, in words you can show. `CancelTurns()` stops the running
  turn and drops the waiting ones.
- **Limits.** A line or event over `MaxLineLength` (1 MiB), a stream that ends in the middle of a
  message, a body that is not UTF-8, and a stream that sends nothing for `ReadTimeout` (2 minutes)
  all fail the turn with a clear error instead of hanging. At most `MaxQueuedTurns` (16) turns
  wait; a waiting turn whose token is cancelled leaves the queue at once. An agent function call
  fails at once when its response ends without the answer, or as cancelled when the client is
  disposed.
- **Threads.** The client works on the synchronization context it was created on, so inject it
  into a component and it processes messages on the renderer's thread.

In WebAssembly the stream is read in the browser, so the agent endpoint must allow your app's
origin (CORS, below). `BlazorA2UI` and `BlazorA2UI.Core` are trimmable, and the WebAssembly
sample's trimmed publish fails on any trim warning from them.

## Projects

| Project | Package | What it is |
| --- | --- | --- |
| `src/BlazorA2UI.Core` | `BlazorA2UI.Core` | The protocol engine. No UI dependency. |
| `tests/BlazorA2UI.Core.Tests` | | Unit and conformance tests. |
| `src/BlazorA2UI` | `BlazorA2UI` | Razor components, the media allowlist, `a2ui.css` and the agent client. |
| `tests/BlazorA2UI.Tests` | | bUnit tests for the components and the rendering safety rules. |
| `src/BlazorA2UI.Agent` | `BlazorA2UI.Agent` | Agent-side builders, writer, validator, LLM prompts and response parsing, and handling of the renderer's messages. No dependency beyond the core. |
| `tests/BlazorA2UI.Agent.Tests` | | Tests for the agent side, including a strict check of every basic component and round trips against the renderer core. |
| `src/BlazorA2UI.Agent.AspNetCore` | `BlazorA2UI.Agent.AspNetCore` | The streaming endpoint for ASP.NET Core. |
| `tests/BlazorA2UI.Agent.AspNetCore.Tests` | | Tests for the endpoint over real HTTP. |
| `samples/SampleAgent` | | A table-booking agent on the streaming endpoint, with a real model or an offline script. See its [README](samples/SampleAgent/README.md). |
| `tests/BlazorA2UI.SampleAgent.Tests` | | Tests for the sample agent over real HTTP with the library's client. |
| `samples/BlazorServerSample` | | A Blazor Server app that chats with the sample agent and shows the media allowlist and stream errors. See its [README](samples/BlazorServerSample/README.md). |
| `tests/BlazorA2UI.BlazorServerSample.Tests` | | bUnit tests for its pages, and tests for its renderer wiring and broken pretend agent. |
| `samples/BlazorWasmSample` | | A standalone Blazor WebAssembly app with the same pages, calling the agent across origins and published with trimming. See its [README](samples/BlazorWasmSample/README.md). |
| `tests/BlazorA2UI.BlazorWasmSample.Tests` | | bUnit tests for its pages, and tests for its renderer wiring and settings. |

## Building UI on the agent side

`BlazorA2UI.Agent` builds messages from typed components instead of hand-written JSON. Nest
components as objects; the builder flattens the tree into the wire format, assigns ids, and
writes bindings (`{"@path": ...}`) and calls (`{"@call": ...}`):

```csharp
using System.Text.Json.Nodes;
using BlazorA2UI.Agent;
using BlazorA2UI.Agent.Basic;

A2UIValue email = A2UIValue.Path("/form/email");
Column form = new()
{
    Children =
    [
        new Text { Content = BasicFunctions.FormatString("Hello, ${/user/name}") },
        new TextField
        {
            Label = "Email",
            Value = email,
            Checks = [new A2UICheck(BasicFunctions.Email(email), "Enter a valid email.")],
        },
        new Button
        {
            Child = new Text { Content = "Subscribe" },
            Action = A2UIAction.Event("subscribe", new Dictionary<string, A2UIValue> { ["email"] = email }),
        },
    ],
};

JsonObject message = A2UIMessages.CreateSurface("signup", form, dataModel: new JsonObject { ["user"] = new JsonObject { ["name"] = "Ada" } });
```

- **Ids.** The root gets `root`. Other components keep the `Id` you set, or get one built from the
  nearest ancestor with an id, such as `root__text_1`. Chosen ids may not contain `__`, so they
  never collide with generated ones. Set an `Id` on any part you will update
  later: `A2UIMessages.UpdateComponents("signup", [newCard])` then replaces that part and leaves
  the rest of the surface alone. `A2UIComponent.Ref("id")` places a component that is already on
  the surface without sending it again.
- **Lists from data.** `A2UIChildList.Template(rowComponent, "/people")` repeats a component for
  each item; inside it, relative paths such as `A2UIValue.Path("name")` read the current item.
- **Writing.** `A2UIMessageWriter` writes messages as JSON Lines (`application/jsonl`) and
  flushes after each one, so the renderer draws as the agent streams.
- **Checking before sending.** `A2UIMessageValidator` runs each message through the renderer core
  with the same catalogs and reports what a renderer would reject. It keeps the surfaces it has
  seen, so a message is checked against the ones before it. Pass `A2UIValidationConfig.Strict` to
  also require that every update leaves a complete tree.

```csharp
using A2UIMessageValidator validator = new();
A2UIValidationResult result = validator.Validate(message);
if (!result.IsValid)
{
    // result.Errors holds the renderer's error codes and paths, ready to hand back to the model.
}
```

Custom components derive from `A2UIComponent` and write their properties through
`A2UIComponentWriter`.

## Asking a model for UI

`A2UIPromptBuilder` writes the part of the system prompt that teaches a model to answer with
A2UI: the rules for A2UI JSON blocks, the v1.0 schemas cut down to what the model may use, each
catalog's instructions and optional examples. `A2UIResponseParser` and `A2UIResponseStream`
read the answer back into text and messages. The format matches Google's Python agent SDK.

```csharp
A2UIPromptBuilder prompt = new([BasicCatalog.Create()]);
prompt.Examples.Add([A2UIMessages.CreateSurface("main", new Text { Content = "Hello" })]);
string systemPrompt = "You are a booking assistant.\n\n" + prompt.Build();

// A complete answer:
foreach (A2UIResponsePart part in A2UIResponseParser.Parse(answer))
{
    switch (part)
    {
        case A2UITextPart text: ShowText(text.Text); break;
        case A2UIMessagePart message: await SendToRendererAsync(message.Message); break;
        case A2UIErrorPart error: Log(error.Error); break; // or hand it back to the model to retry
    }
}

// An answer that streams: each message is returned as soon as its JSON object closes,
// so the renderer can draw while the model is still writing.
A2UIResponseStream stream = new();
await foreach (string chunk in model.StreamAsync(systemPrompt, question))
{
    Handle(stream.Append(chunk));
}

Handle(stream.Complete());
```

- **Only UI messages by default.** The prompt and the parsers allow `createSurface`,
  `updateComponents`, `updateDataModel` and `deleteSurface`. Set `AllowedMessages` on the builder
  and on `A2UIResponseParserOptions` to allow more, such as `callRendererFunction`.
- **Forgiving, never throwing.** The parsers accept what models commonly get wrong: tags in
  another case, Markdown fences, a single message instead of an array, trailing commas, comments,
  raw line breaks in strings, curly quotes and a missing `version`. A close tag inside a JSON string
  does not end a block. A block they cannot read becomes an `A2UIErrorPart`, written so it can be
  passed back to the model.
- **Bounded.** A block may hold at most `MaxBlockLength` characters (1,000,000 by default) and an
  answer at most `MaxMessages` messages (1,000), and JSON may nest no deeper than the renderer
  accepts.
- **Still validate.** The parsers check each message's envelope, not its content. Pass each
  message through `A2UIMessageValidator` before sending it if you want to catch the model's
  mistakes on the agent side; the renderer checks everything again either way.
- **Tool calls.** For a model that returns UI as a tool call's JSON arguments, use
  `A2UIResponseParser.ParseJson`.
- **Agent-only functions** are left out of the prompt's catalogs, since UI the model writes may never
  call them.

## Handling messages from the renderer

The renderer sends four kinds of messages back: `action` (the user did something),
`callAgentFunction` (a binding or action calls a function the agent implements),
`rendererFunctionResponse` (the answer to the agent's own call) and `error`.
`A2UIRendererMessageProcessor` checks each one against the spec and routes it:

```csharp
using BlazorA2UI.Catalogs;

Catalog catalog = Catalog.FromJson(catalogJson); // the catalog the renderer also has

// Agent functions are declared in the catalog without an implementation, so the renderer
// sends their calls to the agent. Arguments are checked against the declared schema first.
A2UIAgentFunctions functions = new A2UIAgentFunctions([catalog])
    .Add(catalog.CatalogId, "lookupOrder", async (call, cancellationToken) =>
        await orders.FindStatusAsync(call.Args["orderId"]!.GetValue<string>(), cancellationToken));

using A2UIRendererFunctionCalls rendererCalls = new(new A2UIRendererFunctionCallsOptions { Catalogs = [catalog], AgentFunctions = functions });

A2UIRendererMessageProcessor processor = new(functions)
{
    OnAction = async (action, cancellationToken) =>
    {
        await RunAgentTurnAsync(action.Name, action.Context, cancellationToken);
        return true;
    },
    OnRendererFunctionResponse = (response, _) => ValueTask.FromResult(rendererCalls.Resolve(response)),
};

A2UIRendererMessageResult result = await processor.ProcessLineAsync(line, cancellationToken);
foreach (JsonObject reply in result.Replies)
{
    await SendToRendererAsync(reply); // agentFunctionResponse messages
}
```

- **Agent functions.** Every `callAgentFunction` gets exactly one `agentFunctionResponse`.
  Unknown functions get `UNKNOWN_FUNCTION`, bad arguments `INVALID_FUNCTION_CALL`. A handler
  can throw `A2UIException` to choose its own code. Any other exception becomes
  `EXECUTION_ERROR` with a generic message, so stack details never reach the browser; subscribe to
  `A2UIAgentFunctions.FunctionFailed` to log them. `Timeout` limits how long a handler runs.
- **Not handled.** A message without a callback, or one the callback declines by returning
  `false`, comes back with `Handled == false`, so you can hand it to the model's turn.
- **Calling renderer functions.** `A2UIRendererFunctionCalls.Call` builds a `callRendererFunction`
  message and returns a task for the value. Arguments may be bindings, which the renderer
  resolves against the surface's data model. With the agent's catalogs, a call the renderer would
  refuse (a `rendererOnly` function, one that needs a user gesture, bad arguments) is refused before
  it is sent. Calls time out after 30 seconds by default.

```csharp
A2UICall call = new("getLocation") { CatalogId = catalog.CatalogId };
A2UIRendererFunctionCall pending = rendererCalls.Call(call, surfaceId: "main");
await SendToRendererAsync(pending.Message);
JsonNode? location = await pending.Result; // throws A2UIException on an error, a timeout or cancellation
```

## Serving an agent over HTTP

`BlazorA2UI.Agent.AspNetCore` maps one `POST` endpoint that runs the agent's turns and streams
their messages back:

```csharp
using BlazorA2UI.Agent.AspNetCore;

app.MapA2UIAgent("/a2ui", async (turn, cancellationToken) =>
{
    // turn.Text is the user's text; turn.Actions and turn.Errors come from the renderer.
    IAsyncEnumerable<string> answer = model.StreamAsync(systemPrompt, turn.Text, cancellationToken);
    IReadOnlyList<A2UIErrorPart> problems = await turn.SendModelResponseAsync(answer, cancellationToken: cancellationToken);
    // problems holds what was not sent, written so it can go back to the model.
}, options =>
{
    options.Catalogs.Add(BasicCatalog.Create());
    options.AgentFunctions = functions; // optional
}).RequireAuthorization();
```

**The request** is `application/json`. Every part is optional, but a request holds text or
messages:

```json
{ "text": "Book a table for two", "messages": [ /* action, callAgentFunction, rendererFunctionResponse, error */ ], "metadata": { } }
```

**The response** streams the agent's messages as JSON Lines (`application/jsonl`), one message per
line, each flushed as soon as it is ready. A client that prefers `text/event-stream` in its `Accept`
header (by quality, over `application/jsonl`) gets server-sent events instead: `a2ui` events with messages, `text` events with the model's text, and
an `error` event if the turn fails. The turn ends when the response ends.

- **Conversations.** The first response carries a new id in the `A2UI-Conversation` header. Send it
  back with each later request. The server keeps the conversation's surfaces, so every message is
  checked the way the renderer will check it, and its pending renderer function calls. Idle
  conversations expire after `ConversationIdleTimeout` (30 minutes). An unknown or expired id gets
  404, and a full store (`MaxConversations`, 10,000) gets 503. A conversation started by a signed-in
  user belongs to that user (`ConversationOwner`), and anyone else gets 404 for its id.
- **Renderer messages.** `callAgentFunction` is answered on the response without a turn, and
  `rendererFunctionResponse` settles the call it answers. `action` and `error` start a turn, as
  text does. A second turn while one is running in the same conversation gets 409.
- **Calling the renderer.** `turn.CallRendererFunctionAsync` sends `callRendererFunction` and
  waits. The client answers with `rendererFunctionResponse` in another request of the same
  conversation, while the first response is still streaming.
- **Checked before sending.** `SendAsync` throws `A2UIValidationException` for a message the
  renderer would reject, and `SendModelResponseAsync` holds such messages back and returns them as
  problems. Only the four UI messages can be sent this way.
- **Failures.** An exception in the turn is logged. The client gets an `error` event with a generic
  message over server-sent events, or an aborted connection over JSON Lines, so a cut-off answer
  never looks complete. Exception details never reach the client.
- **Bounded.** The body is at most `MaxRequestBodySize` (1 MiB) and `MaxMessagesPerRequest` (100)
  messages, with duplicate keys, deep nesting and invalid Unicode refused with 400.
- **Browsers on another origin** need a CORS policy that allows the app's origin and exposes the
  `A2UI-Conversation` header: `policy.WithOrigins("https://my-app").AllowAnyHeader().WithMethods("POST").WithExposedHeaders("A2UI-Conversation")`.

## Using the core directly (available now)

```csharp
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

MessageProcessor processor = new([BasicCatalog.Create()]);

// Renderer-to-agent messages (actions, agent function calls, errors) go to your transport.
processor.Outbound = message => transport.SendAsync(message.Message, message.Metadata);

// Feed agent-to-renderer messages as they stream in (JSONL lines, arrays or single objects).
// Malformed input never throws: it is reported through processor.ErrorReported.
processor.ProcessLine(line);

Surface surface = processor.Surfaces["main"];
if (surface.Root is { } root)                    // the component tree starts here
{
    A2UIBinding text = new(surface, root.Properties["text"], surface.RootContext);
    text.Refresh();                              // evaluates bindings and function calls
    surface.Changed += () => text.Refresh();     // re-evaluate when data or agent answers change
}
```

By default the processor behaves like a streaming renderer: components may arrive in any order,
unknown component types are kept (and shown as placeholders), and functions the catalog does not
implement are routed to the agent with `callAgentFunction`. Set
`A2UIProcessorOptions.Validation = A2UIValidationConfig.Strict` to validate finished payloads
instead (root present, no dangling references or orphans, property schemas, declared functions).

## Conformance

The tests run the language-independent conformance suites from the A2UI repository
(`conformance/core`): expressions, functions, data model and pointers, data context, reserved
keys, validation results, `@index`, the v1.0 message processor and validator, composition rules,
renderer error responses, multiple catalogs, `addCatalog` and agent function routing.

```sh
dotnet test
```

## Requirements

.NET 10 SDK.

## License

MIT, see [LICENSE](LICENSE). Parts of the A2UI specification, a port of its expression parser
and regex checks, and its conformance suites are used under the Apache License 2.0; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
