# blazor-a2ui

A Blazor renderer for [A2UI](https://a2ui.org) **v1.0**, the Agent-to-UI protocol: an agent
streams JSON messages that describe a UI, and the renderer draws it with native components,
keeps a data model, and sends the user's actions back.

> **Status: preview.** The protocol core (`BlazorA2UI.Core`) is merged and passes Google's
> conformance suites. The Razor components and the agent-side helpers are being built next, so
> the sections below marked *planned* describe the API they will have. Names may still change
> before the first release.
>
> | Part | Package | State |
> | --- | --- | --- |
> | Protocol core: messages, data model, bindings, functions, catalogs, validation, agent and renderer function calls | `BlazorA2UI.Core` | Done |
> | Blazor components: the basic catalog as Razor components, with the safety rules below | `BlazorA2UI` | Planned (PR 2) |
> | Agent side: message builders, a streaming endpoint, and sample apps | `BlazorA2UI.Agent` | Planned (PR 3) |

## Why a new renderer

[23min/a2ui-blazor](https://github.com/23min/a2ui-blazor) was the first Blazor renderer for A2UI,
but it targets v0.8/v0.9 and has not moved since early 2026. v1.0 changed the wire format in ways
that are not a rename: `@path` / `@call` / `@index` keys, envelope keys such as `@catalogId` and
`@checks`, the surface theme removed, functions callable in both directions
(`callAgentFunction`, `callRendererFunction`), and per-component catalogs. This project implements
v1.0 from the specification and checks itself against Google's shared conformance suite.

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

## Hosting models

The core has no UI dependency, and the components use only standard Blazor features: no
JavaScript interop and no inline styles. So the same packages are meant to work in every Blazor
hosting model:

| Hosting model | Supported |
| --- | --- |
| Blazor Server (interactive server rendering) | Yes (planned with PR 2) |
| Blazor WebAssembly, standalone or hosted | Yes (planned with PR 2) |
| Blazor Web App with `InteractiveAuto` | Yes (planned with PR 2) |
| Static server-side rendering (no interactivity) | Display only: surfaces render, but inputs and actions need an interactive render mode |
| .NET MAUI Blazor Hybrid | Expected to work, not tested yet |

The core alone (`BlazorA2UI.Core`) runs anywhere .NET 10 runs, including a plain server process
that validates or pre-processes agent output.

## Components

The basic catalog from the A2UI v1.0 specification
(`https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json`) has 18 components. The core
already parses and validates all of them. Their Razor versions come with PR 2.

| Category | Components | Notes |
| --- | --- | --- |
| Display | `Text`, `Icon`, `Divider` | `Text` renders plain text. Markdown, if added, will never load images or links on its own. |
| Media | `Image`, `Video`, `AudioPlayer` | Rendered only through the host's media allowlist; otherwise a blocked placeholder. |
| Layout | `Row`, `Column`, `List`, `Card`, `Tabs`, `Modal` | `justify`, `align` and the like map to CSS classes. `List` supports templates with `@index`. |
| Input | `Button`, `TextField`, `CheckBox`, `ChoicePicker`, `Slider`, `DateTimeInput` | Two-way bound to the data model, with `@checks` validation messages. |

The catalog's 14 functions are implemented in the core: `required`, `regex`, `length`,
`numeric`, `email`, `formatString`, `formatNumber`, `formatCurrency`, `formatDate`, `pluralize`,
`openUrl`, `and`, `or` and `not`. Any other function a message calls is sent to the agent with
`callAgentFunction`.

Custom catalogs and components are supported by the core: register a `Catalog` with your own
component and function definitions. Mapping a custom component to your own Razor component is
planned for PR 2.

## Getting started in a Blazor app (planned, PR 2)

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
| `src/BlazorA2UI` | `BlazorA2UI` | Razor components. Planned (PR 2). |
| `src/BlazorA2UI.Agent` | `BlazorA2UI.Agent` | Agent-side builders and endpoint. Planned (PR 3). |
| `samples/` | | Blazor Server and WebAssembly sample apps and a sample agent. Planned (PR 3). |

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
