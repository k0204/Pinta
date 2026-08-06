# Project Rules

- Layer editing is tree-first. A layer can be both a container and a child node, similar to Unity's Hierarchy.
- Do not add compatibility branches for the old flat layer model. Remove or replace flat-layer assumptions instead of preserving them.
- Prefer one authoritative layer tree API over parallel flat and tree APIs, to keep maintenance cost low.
- Before compiling, stop any existing Pinta or .NET Host process from the previous run so it cannot lock build outputs.
- At the start of every task, invoke the `ponytail` skill before taking other task actions.
- Apply the `ponytail` rule: reuse existing helpers and platform APIs, avoid speculative abstractions and dependencies, and ship the smallest complete change with a focused check.
- Keep `doc/api.md` current. Every new or changed API endpoint, request, response, auth requirement, or default API base URL must be documented in the same change.
- Do not create or write test files.
- All user-visible text must use `Translations.GetString`, including static and formatted text in menu labels, command labels and tooltips, buttons, dialogs, status text, history text, and errors. Translate the message template before adding dynamic values; user-entered content, document names, file paths, internal IDs, and AI prompt content remain unmodified.
- Do not use a translated language as the source message key. Use a stable English message key so every locale can translate it through the normal gettext extraction workflow.
- Internal IDs, metadata keys, file paths, brand names, dynamic user data, and AI prompt content are not UI strings and must not be translated automatically.
- When adding or changing UI strings, update the gettext extraction inputs and translation catalogs through the project's existing translation workflow.

## Source Organization and Size

- Organize new functionality by feature and responsibility. Do not add unrelated behavior to an existing catch-all file merely because the owning type is already declared there.
- When an existing owner is a `partial` class, put a substantial new feature in `TypeName.FeatureName.cs` (for example, `LayerActions.Spritesheet.cs`). Keep shared state and lifecycle wiring in the main `TypeName.cs` file.
- Keep one authoritative implementation for each behavior. Do not create generic `Helpers`, `Utils`, or `Common` files unless the code is genuinely shared by multiple features.
- A new or materially expanded source file should target at most 500 lines and must not exceed 800 lines. Existing files above the limit may remain, but new feature code must not make them larger; extract the touched responsibility into a focused file instead.
- A method should target at most 60 lines and must not exceed 100 lines. Split validation, data transformation, UI construction, and side effects into focused methods when they form distinct responsibilities.
- Declarative UI layout, static mappings, or serialization declarations may exceed the method target only when splitting would reduce readability. They still must respect the 100-line maximum unless the exception and reason are explicitly documented in the change summary.
- Line counts include comments and blank lines. Do not compress formatting or combine statements merely to satisfy a limit.
