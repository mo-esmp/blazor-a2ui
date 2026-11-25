# blazor-a2ui

A Blazor renderer for [A2UI](https://a2ui.org) **v1.0**, the Agent-to-UI protocol: an agent
streams JSON messages that describe a UI, and the renderer draws it with native components,
keeps a data model, and sends the user's actions back.

> **Status: preview, in progress.** This repository is being built as a series of pull requests:
>
> 1. **Protocol core** (`BlazorA2UI.Core`): messages, data model, bindings, functions, catalogs,
>    validation and agent/renderer function calls. *This PR.*
> 2. **Blazor components** (`BlazorA2UI`): the basic catalog as Razor components, with the safety
>    rules below.
> 3. **Agent side and samples** (`BlazorA2UI.Agent`): message builders, a streaming endpoint and a
>    sample app.

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

## Projects

| Project | Package | What it is |
| --- | --- | --- |
| `src/BlazorA2UI.Core` | `BlazorA2UI.Core` | The protocol engine. No UI dependency. |
| `tests/BlazorA2UI.Core.Tests` | | Unit and conformance tests. |

## Using the core

```csharp
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

var processor = new MessageProcessor([BasicCatalog.Create()]);

// Renderer-to-agent messages (actions, agent function calls, errors) go to your transport.
processor.Outbound = async message => await transport.SendAsync(message.Message, message.Metadata);

// Feed agent-to-renderer messages as they stream in (JSONL lines, arrays or single objects).
processor.ProcessLine(line);

var surface = processor.Surfaces["main"];
var root = surface.Root;                         // the component tree starts here
var text = new A2UIBinding(surface, root!["text"], surface.RootContext);
text.Refresh();                                  // evaluates bindings and function calls
surface.Changed += () => text.Refresh();         // re-evaluate when data or agent answers change
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
