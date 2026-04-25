# Authors

## Original

`SpireLens MCP` is a fork of [`Gennadiyev/STS2MCP`](https://github.com/Gennadiyev/STS2MCP) (mod ID `STS2_MCP`), authored by **Yikun Ji (Kunologist)**, vendored at upstream version **0.3.4** under the MIT license. The original `LICENSE` file is preserved verbatim — all of the C# mod code (HTTP server, state builder, action handlers, formatting, multiplayer surface) and the Python MCP server in `mcp/` originate from that project.

## Fork

This fork is maintained by **nelsong6** as the live-validation bridge for [SpireLens](https://github.com/nelsong6/spirelens). The vendor was performed on 2026-04-24 because the SpireLens roadmap needs reinforcement-learning-shaped extensions (decision-point state snapshots, custom event-stream tools, run-archive search) that don't fit the original project's scope, and tracking those as patches against an upstream we don't control would be more work than owning the fork.

The initial vendor commit is a near-pure rename:

- mod ID `STS2_MCP` → `SpireLensMcp`
- C# namespace `STS2_MCP` → `SpireLens.Mcp`
- csproj/sln/manifest filenames updated
- log prefix `[STS2 MCP]` → `[SpireLens MCP]`
- HTTP greeting `Hello from STS2 MCP v…` → `Hello from SpireLens MCP v…`
- README rewritten to describe the SpireLens fork
- Port (`15526`), API surface (`/api/v1/singleplayer`, `/api/v1/multiplayer`), tool names, and behavior — **unchanged** from upstream

Future commits are free to diverge.
