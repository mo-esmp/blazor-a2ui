# blazor-a2ui

[![CI](https://github.com/mo-esmp/blazor-a2ui/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mo-esmp/blazor-a2ui/actions/workflows/ci.yml)

A Blazor renderer for [A2UI](https://a2ui.org) **v1.0**, the Agent-to-UI protocol: an agent
streams JSON messages that describe a UI, and the renderer draws it with native components,
keeps a data model, and sends the user's actions back.

> **Status: preview.** The protocol core (`BlazorA2UI.Core`) passes Google's conformance
> suites, and the Razor components (`BlazorA2UI`) render the whole basic catalog. The agent side
> (`BlazorA2UI.Agent`) has its builders, validator and the messages that flow back from the
> renderer; LLM prompts and the streaming endpoint are in progress. Names may
> still change before the first release.
>
> | Part | Package | State |
> | --- | --- | --- |
> | Protocol core: messages, data model, bindings, functions, catalogs, validation, agent and renderer function calls | `BlazorA2UI.Core` | Done |
> | Blazor components: the basic catalog as Razor components, with the safety rules below | `BlazorA2UI` | Done |
> | Agent side: component and message builders, a JSONL writer, a validator that mirrors the renderer | `BlazorA2UI.Agent` | Done |
> | Agent side: messages from the renderer, agent functions, renderer function calls | `BlazorA2UI.Agent` | Done |
> | Agent side: LLM prompts and response parsing | `BlazorA2UI.Agent` | Planned |
> | Streaming endpoint for ASP.NET Core, and sample apps | `BlazorA2UI.Agent.AspNetCore` | Planned |

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

**5. Stream the agent's messages** into the scoped processor:

```csharp
@inject MessageProcessor Processor
@inject HttpClient Http

protected override async Task OnInitializedAsync()
{
    using HttpResponseMessage response = await Http.GetAsync(
        "https://my-agent/stream", HttpCompletionOption.ResponseHeadersRead);
    using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
    while (await reader.ReadLineAsync() is { } line)
    {
        Processor.ProcessLine(line);   // <A2UISurface> re-renders as messages arrive
    }
}
```

In Blazor Server the stream is read on the server; in WebAssembly it is read in the browser, so
the agent endpoint must allow your app's origin (CORS).

## Projects

| Project | Package | What it is |
| --- | --- | --- |
| `src/BlazorA2UI.Core` | `BlazorA2UI.Core` | The protocol engine. No UI dependency. |
| `tests/BlazorA2UI.Core.Tests` | | Unit and conformance tests. |
| `src/BlazorA2UI` | `BlazorA2UI` | Razor components, the media allowlist and `a2ui.css`. |
| `tests/BlazorA2UI.Tests` | | bUnit tests for the components and the rendering safety rules. |
| `src/BlazorA2UI.Agent` | `BlazorA2UI.Agent` | Agent-side builders, writer, validator, and handling of the renderer's messages. No dependency beyond the core. |
| `tests/BlazorA2UI.Agent.Tests` | | Tests for the agent side, including a strict check of every basic component and round trips against the renderer core. |
| `src/BlazorA2UI.Agent.AspNetCore` | `BlazorA2UI.Agent.AspNetCore` | Streaming endpoint for ASP.NET Core. Planned. |
| `samples/` | | Blazor Server and WebAssembly sample apps and a sample agent. Planned. |

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
