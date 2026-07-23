# Project Rules

- Layer editing is tree-first. A layer can be both a container and a child node, similar to Unity's Hierarchy.
- Do not add compatibility branches for the old flat layer model. Remove or replace flat-layer assumptions instead of preserving them.
- Prefer one authoritative layer tree API over parallel flat and tree APIs, to keep maintenance cost low.
