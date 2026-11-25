# Third-party notices

This project is licensed under the MIT License (see [LICENSE](LICENSE)). It includes material
from the projects below, under their own licenses.

## A2UI (Google LLC), Apache License 2.0

Source: https://github.com/a2ui-project/a2ui (formerly github.com/google/A2UI), commit
`a23b19c4f2102695c506adb7d1ded1f5f81a7688`.

Copyright 2024–2026 Google LLC. Licensed under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0). A copy of the license is in
[`tests/BlazorA2UI.Core.Tests/Conformance/data/LICENSE`](tests/BlazorA2UI.Core.Tests/Conformance/data/LICENSE).

Used here:

| What | Where in this repository | Changes |
| --- | --- | --- |
| Basic catalog v1 (`catalogs/basic/v1/catalog.json`) | `src/BlazorA2UI.Core/Catalogs/Basic/catalog.json` | None (embedded resource) |
| v1.0 common types schema (`specification/v1_0/json/common_types.json`) | `src/BlazorA2UI.Core/Schemas/common_types.json` | None (embedded resource) |
| Expression template parser (`typescript/core/web_core/src/expressions/expression_parser.ts`) | `src/BlazorA2UI.Core/Expressions/ExpressionParser.cs` | Ported to C# |
| A2UI regex dialect and safety checks (`typescript/core/web_core/src/common/regex_subset.ts`) | `src/BlazorA2UI.Core/Expressions/A2UIRegex.cs` | Ported to C#; emits .NET regular expressions |
| Conformance suites (`conformance/core/*.yaml`, `conformance/test_data/catalogs/*.json`) | `tests/BlazorA2UI.Core.Tests/Conformance/data/` | None (test data) |

The ported files carry a header naming their source.

## a2ui-blazor (23min), MIT License

Source: https://github.com/23min/a2ui-blazor. Copyright (c) 2025 23min.

The earlier Blazor renderer for A2UI v0.8/v0.9. This project is a rewrite for v1.0 and contains
no code copied from it; its structure (a core message processor plus a Razor component library
and an agent-side package) informed the layout here.
