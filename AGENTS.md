# Agent Guidelines

## Project

* A Blazor renderer for [A2UI](https://a2ui.org) **v1.0**. An agent streams JSON messages that describe a UI. The renderer
  draws it with native components, keeps a data model, and sends the user's actions back.
* `src/BlazorA2UI.Core` (package `BlazorA2UI.Core`) is the protocol engine: messages, data model, bindings, expressions,
  functions, catalogs, validation, and calls in both directions (`callAgentFunction` and `callRendererFunction`). It has
  no UI dependency.
* `src/BlazorA2UI` (package `BlazorA2UI`) holds the basic catalog as Razor components and enforces the rendering rules
  below.
* `src/BlazorA2UI.Agent` (package `BlazorA2UI.Agent`) is for the agent side: component and message builders, a JSONL
  writer, a validator, the LLM prompt builder and response parsers, the processor for messages from the renderer
  (actions, errors, agent functions) and renderer function calls. It depends on nothing but the core. The ASP.NET Core streaming endpoint goes in a separate
  package, `BlazorA2UI.Agent.AspNetCore`.
* `tests/BlazorA2UI.Core.Tests` holds the unit tests and the conformance tests. `tests/BlazorA2UI.Tests` and
  `tests/BlazorA2UI.Agent.Tests` test the components and the agent side.

## Safety

A model writes the UI, so every value in a message is untrusted input. These rules are not negotiable, and a pull request
that weakens one is rejected:

* Opening an answer never makes the browser fetch anything. `Image`, `Video` and `AudioPlayer` never put a model-supplied
  URL into the page. Media resolve only through an allowlist that the host registers. Without one, media render as a
  blocked placeholder.
* No model value ever reaches an inline `style`, an attribute name, raw HTML (`MarkupString`) or JavaScript. Layout
  options (`justify`, `align`, `variant`, `fit` and the like) are enumerations mapped to CSS classes. A value outside the
  enumeration is ignored.
* `openUrl` accepts only `http` and `https`, runs only from a user action, and goes only through the host's
  `IA2UIUrlLauncher`. Without a launcher it does nothing.
* Regular expressions from a message use the A2UI regex dialect (`A2UIRegex`): no backreferences or lookaround, checks
  for nested quantifiers and overlaps, length limits, and a match timeout. Never pass a model pattern to `Regex` directly.
* Every message is bounded: nesting depth, component counts, tree depth, list indices, pending agent calls and call
  timeouts. A new feature that walks or stores model data gets a limit as well.
* The core enforces the protocol-level rules (URL schemes, user activation, regex safety, limits). The component library
  enforces the rendering rules. Every safety rule has a test that proves it holds.

## Design

* Follow the .NET Framework Design Guidelines and library-design best practices.
* Performance and memory come first: prefer the faster, lower-allocation design when it conflicts with convenience.
* `internal` by default. A type becomes public only when a consumer needs it. Tests and sibling packages reach internals
  through `InternalsVisibleTo`.
* Classes are `sealed` unless they are designed for inheritance. A change to a shipped public API is a breaking change.
* `BlazorA2UI.Core` depends on nothing outside the .NET base class library. A new package dependency in any `src`
  project needs the owner's approval. Versions are pinned centrally in `Directory.Packages.props`.

## Hot Paths

These run on every streamed message or every render: message processing (`MessageProcessor`, `ComponentParser`), binding
evaluation (`A2UIEvaluator`, `A2UIBinding`), data-model reads, writes and observer notification (`DataModel`,
`JsonPointer`), and component rendering.

* Avoid allocations that don't need to happen: no LINQ, closures, boxing or string concatenation in per-render or
  per-binding loops. Cache parsed expressions and compiled patterns, not results.
* Re-evaluate only what changed. Observers are scoped to their path, and an unchanged value raises no change.
* A performance claim needs a BenchmarkDotNet run with `[MemoryDiagnoser]`, before and after.

## Errors

* Malformed agent input never throws out of `Process` or `ProcessLine`. It becomes an A2UI error report: the
  `ErrorReported` event and, when `A2UIProcessorOptions.SendErrorsToAgent` is on, an `error` message to the agent with the spec's code (`VALIDATION_FAILED`,
  `CATALOG_NOT_FOUND`, `INVALID_FUNCTION_CALL` and the others in `A2UIErrorCodes`), the surface id and the JSON
  Pointer path. `ProcessOrThrow` is the one entry point that throws, and it exists for tests and validators.
* Inside the core, an `A2UIException` subclass carries the error code and path to the point where it is reported.
  Exceptions of any other type are only for caller bugs.
* A rejected message changes nothing. A failed write, a rejected `createSurface` or a failed component update leaves
  the surface and the data model as they were.
* Argument checks use the throw helpers: `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIfNegative`
  and the like.

## Code Style

Follow `.editorconfig`. These rules add to it:

* File-scoped namespaces. The root namespace is `BlazorA2UI` for every project, followed by the folder path, so
  `src/BlazorA2UI.Core/Catalogs` is `BlazorA2UI.Catalogs` and `tests/BlazorA2UI.Core.Tests/Conformance` is
  `BlazorA2UI.Tests.Conformance`. Project-wide `global using`s go only in the project's `GlobalUsings.cs`.
* Primary constructors for services, and constructor injection only.
* Name the type on the left and infer it on the right. Never use `var`, and use target-typed `new`
  (`DataModel model = new(initial);`). Both are build errors. Where the left side names no type, spell it out
  (`return new JsonObject();`, `List<string> ids = [];`).
* Private and internal static fields start with `s_`. Use expression-bodied members for one-liners, braces on every
  control statement, guard clauses and early returns. Put a blank line after each property when a class has more than
  one.
* Async: method names end in `Async`. Use `ValueTask` on hot paths and `ConfigureAwait(false)` in library code.
  `CancellationToken` is the last parameter. Never use `async void`, `.Result` or `.Wait()`. Razor components are the
  exception to `ConfigureAwait(false)`, because they must resume on the renderer's synchronization context.
* Nullable reference types are on. Never use `!` without a comment that says why it is safe.
* Document all code. Every type and every member has XML documentation, whatever its accessibility (public, internal or
  private) and whichever project it is in (`src`, `tests`, `bench`). A test's summary says in one sentence what it
  proves. Say what the signature cannot: what a `false` or an error result means, what throws and why, and whether a
  failed call changes state. Don't use one-line XML doc comments. Use the multi-line form:

  ```csharp
  /// <summary>
  /// This is a sample
  /// </summary>
  ```

* `.cs` files are UTF-8 with a BOM and LF line endings, as `.editorconfig` sets.
* Generated files (`Unicode/Uax31Tables.cs`) carry an `<auto-generated>` header and are regenerated by their script in
  `tools/`, never edited by hand.

## Testing

* Use xUnit, Shouldly and NSubstitute, with Arrange, Act and Assert comments in every test.
* Test names follow `MethodName_StateUnderTest_ExpectedBehavior`, for example
  `Set_IndexAboveLimit_LeavesModelUnchanged`.
* Protocol behavior is tested against the language-independent A2UI conformance suites in
  `tests/BlazorA2UI.Core.Tests/Conformance/data`, which are vendored from the A2UI repository. A data-driven conformance
  theory follows the same pattern, with the method it exercises and `ConformanceCase_MatchesSuite`
  (`Parse_ConformanceCase_MatchesSuite`). Every case runs, and a case may be skipped only with a comment that cites the
  conformance README rule allowing it. Missing fields in a case fail through `FixtureGuard.Required()`, not `!`.
* Test files mirror the source folders (`tests/BlazorA2UI.Core.Tests/Data/DataModelTests.cs` tests
  `src/BlazorA2UI.Core/Data/DataModel.cs`), and test classes are `sealed`.
* Behavior that the suites don't cover, including every safety rule and every fixed bug, gets a regular unit test.
* Unit tests use no network, sockets, browser or real agent.

## Build & Docs

* Before every commit, run `dotnet build -c Release` with 0 warnings and get a green `dotnet test`.
* The A2UI v1.0 specification is the authority, at the commit pinned in `THIRD-PARTY-NOTICES.md`, together with its
  conformance suites. When the spec moves, update the vendored suites and the pinned commit in the same pull request.
  Code that has to diverge from the spec says why in a comment and in `README.md`.
* Third-party code or data (spec text, ported parsers, conformance suites) keeps its license and is listed in
  `THIRD-PARTY-NOTICES.md`.

## Branches, Commits & Pull Requests

* Branch: `feature/<issue-number>-<brief-issue-title>`, in kebab-case, for example `feature/12-image-allowlist`.
* Commit message: one imperative sentence ending with a period, for example `Add the basic catalog's Image component.`
* Pull request title: `#<IssueNumber> - <IssueTitle>.` The squash commit on `main` takes this title, with the PR number
  appended. The description links the issue with `Closes #<IssueNumber>`.
