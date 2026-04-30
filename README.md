# SpireLens MCP

HTTP bridge mod for [**Slay the Spire 2**](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) used by [SpireLens](https://github.com/nelsong6/spirelens) for live-game agentic validation. Exposes game state and actions on `localhost:15526` for the bundled Python MCP server, which Claude Code and other MCP clients can use to drive runs.

Vendored from [`Gennadiyev/STS2MCP`](https://github.com/Gennadiyev/STS2MCP) v0.3.4 under MIT — see [`AUTHORS.md`](AUTHORS.md) for attribution and the rationale for forking.

## Components

- **C# mod** (`McpMod.*.cs`, [`SpireLensMcpBridge.csproj`](SpireLensMcpBridge.csproj)) — built into `SpireLensMcpBridge.dll`, copied to `<game_install>/mods/`. Opens an `HttpListener` on `localhost:15526` and dispatches to handlers for game-state queries and action commands.
- **Python MCP server** (`mcp/server.py`) — stdio MCP server that connects to the in-game listener and exposes ~50 tools (`combat_play_card`, `relic_select`, `get_game_state`, etc.). Used by Claude Code via the consuming repo's `.mcp.json`.

## Build

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and the base game.

```powershell
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
# or
$env:STS2_GAME_DIR = "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
.\build.ps1
```

Outputs `out/SpireLensMcpBridge/SpireLensMcpBridge.dll`. Install:

```
out/SpireLensMcpBridge/SpireLensMcpBridge.dll  ->  <game_install>/mods/SpireLensMcpBridge/SpireLensMcpBridge.dll
mod_manifest.json                              ->  <game_install>/mods/SpireLensMcpBridge/SpireLensMcpBridge.json
```

(The mod loader expects a folder named after the mod ID with the manifest renamed to `<id>.json`. The mod ID is `SpireLensMcpBridge` per `mod_manifest.json`.)

## Verify

With the game running:

```bash
curl -s http://localhost:15526/
```

Expected:

```json
{"message": "Hello from SpireLens MCP v0.3.4", "status": "ok"}
```

`Connection refused` means the mod isn't loaded — check that mods are enabled in the game's settings.

## MCP server

Requires [Python 3.11+](https://www.python.org/) and [uv](https://docs.astral.sh/uv/). First run installs the locked dependencies from `mcp/uv.lock`:

```bash
uv run --directory /path/to/spire-lens-mcp/mcp python server.py --help
```

Add to a consumer's `.mcp.json` (e.g. SpireLens's) — pointing at the local checkout:

```json
{
  "mcpServers": {
    "sts2-modding": {
      "type": "stdio",
      "command": "uv",
      "args": ["run", "--directory", "D:\\repos\\spire-lens-mcp\\mcp", "python", "server.py"],
      "env": {
        "STS2_GAME_DIR": "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2"
      }
    }
  }
}
```

## License

MIT — see [`LICENSE`](LICENSE).

The original copyright notice (Yikun Ji / Kunologist, 2026) is preserved in `LICENSE` per the MIT terms. Modifications by nelsong6 are documented in `AUTHORS.md` and the git history of this repo.
