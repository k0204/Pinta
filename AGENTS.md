# Project Rules

- Layer editing is tree-first. A layer can be both a container and a child node, similar to Unity's Hierarchy.
- Do not add compatibility branches for the old flat layer model. Remove or replace flat-layer assumptions instead of preserving them.
- Prefer one authoritative layer tree API over parallel flat and tree APIs, to keep maintenance cost low.
- Before compiling, stop any existing Pinta or .NET Host process from the previous run so it cannot lock build outputs.
- At the start of every task, invoke the `ponytail` skill before taking other task actions.
- Apply the `ponytail` rule: reuse existing helpers and platform APIs, avoid speculative abstractions and dependencies, and ship the smallest complete change with a focused check.
- Do not create or write test files.
