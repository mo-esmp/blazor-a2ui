# Security Policy

## Supported Versions

BlazorA2UI is in pre-release. Security fixes go into the latest `0.x` pre-release only.

| Version                  | Supported |
| ------------------------ | --------- |
| 0.x (latest pre-release) | Yes       |
| Older pre-releases       | No        |

## Reporting a Vulnerability

If you find a security vulnerability, please report it privately:

1. **Do not** open a public issue, discussion or pull request.
2. Use GitHub's [private vulnerability reporting](https://github.com/mo-esmp/blazor-a2ui/security/advisories/new).
3. Include a description of the vulnerability, the A2UI messages or steps that reproduce it, the package and version,
   and the potential impact.

We will acknowledge your report within 48 hours and aim to release a fix within 7 days for critical issues. We will
credit you in the advisory unless you ask us not to.

A bypass of any rule in the security model below is a vulnerability, even when no exploit is known.

## Security Model

A model writes the UI, so BlazorA2UI treats every value in an A2UI message as untrusted input. It follows the A2UI v1.0
protocol's security model and adds rendering rules of its own.

- **No executable code from agents.** Agents send declarative component trees and data, never HTML, JavaScript or
  executable code. No model value ever reaches raw HTML (`MarkupString`), JavaScript, an attribute name or an inline
  `style`.
- **Pre-approved component catalog.** Only component types that the surface's catalog declares and that have a
  registered renderer are rendered. A type the catalog doesn't declare renders as an inert "Unsupported component"
  placeholder, and strict validation (`A2UIValidationConfig.Strict`) rejects it. A type the catalog declares but the
  host turned off (`options.Components.Unregister`) passes validation and also renders as the placeholder. Layout options (`justify`, `align`, `variant`,
  `fit` and the like) are enumerations mapped to fixed CSS classes; a value outside the enumeration falls back to the
  catalog default.
- **Opening an answer never makes the browser fetch anything.** `Image`, `Video` and `AudioPlayer` never put a
  model-supplied URL into the page. Media resolve only through an allowlist that the host registers
  (`IA2UIMediaResolver`). Without one, media render as a blocked placeholder.
- **Data binding is scoped to the surface.** Bindings (`{"@path": ...}`) read from the surface's own data model. Input
  components (`TextField`, `CheckBox`, `ChoicePicker`, `Slider`, `DateTimeInput`) are two-way bound and write only the
  user's input, only to the path they are bound to, in that surface's data model. The data model goes back to the
  agent only for surfaces whose `createSurface` asked for it with `sendDataModel`.
- **Actions are explicit.** A user interaction is either an `event` sent to the agent as a structured action (a name and
  a context resolved from the data model) or a `functionCall` to a function the catalog declares. A function the
  catalog does not implement is sent to the agent with `callAgentFunction`, never run in the browser.
- **Navigation needs the user and the host.** `openUrl` accepts only absolute `http` and `https` URLs, runs only from
  a user action, and goes only through the host's `IA2UIUrlLauncher`. Without a launcher it does nothing.
- **Safe regular expressions.** Patterns from a message use the A2UI regex dialect (`A2UIRegex`): no backreferences or
  lookaround, checks for nested quantifiers and overlaps, length limits and a match timeout.
- **Bounded input.** Nesting depth, component counts per surface, tree depth, list indices, pending agent calls and
  call timeouts all have limits (`A2UIProcessorOptions`).
- **Malformed input is contained.** A malformed message never throws out of the message processor. It becomes an A2UI
  error report, and a rejected message leaves the surface and its data model as they were.

The core (`BlazorA2UI.Core`) enforces the protocol-level rules: URL schemes, user activation, regex safety and limits.
The component library (`BlazorA2UI`) enforces the rendering rules. Every rule has a test that proves it holds.

## Your Responsibilities as a Host

BlazorA2UI cannot protect what it does not control:

- Allow only media URLs you trust in your `IA2UIMediaResolver`, and only links you are willing to open in your
  `IA2UIUrlLauncher`.
- Treat actions, agent function calls and data models from the renderer as untrusted input on the agent side too.
  A user can edit anything the browser sends.
- Register custom components and functions with the same care: they run with your app's permissions and must follow the
  rules above.
