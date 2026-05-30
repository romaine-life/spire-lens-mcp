# SpireLens MCP

HTTP bridge mod for [**Slay the Spire 2**](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) used by [SpireLens](https://github.com/nelsong6/spirelens) for live-game agentic validation. Exposes game state and actions on `localhost:15526` for the bundled Python MCP server, which Claude Code and other MCP clients can use to drive runs.

Vendored from [`Gennadiyev/STS2MCP`](https://github.com/Gennadiyev/STS2MCP) v0.3.4 under MIT — see [`AUTHORS.md`](AUTHORS.md) for attribution and the rationale for forking.

## Components

- **C# mod** (`McpMod.*.cs`, [`SpireLensMcpBridge.csproj`](SpireLensMcpBridge.csproj)) — built into `SpireLensMcpBridge.dll`, copied to `<game_install>/mods/`. Opens an `HttpListener` on `localhost:15526` and dispatches to handlers for game-state queries and action commands.
- **Python MCP server** (`mcp/server.py`) — MCP server that connects to the in-game listener and exposes ~50 tools (`combat_play_card`, `relic_select`, `get_game_state`, etc.). Used by Claude Code via the consuming repo's `.mcp.json`. Speaks `stdio` by default; pass `--transport http` to serve remote clients (see [Remote clients](#remote-clients-http-transport)).

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

> **Build breaking after an STS2 update?** The game ships roughly every two weeks and routinely renames or removes the C# game symbols this bridge compiles and reflects against. Before re-diagnosing from scratch, read [**Surviving STS2 updates**](https://github.com/nelsong6/spirelens/blob/main/docs/surviving-sts2-updates.md) in the SpireLens repo — it catalogs the recurring failure modes (build drift, BaseLib desync / frozen HUD, debug-load HUD desync) with concrete symptom → cause → fix.

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

### Remote clients (HTTP transport)

`stdio` requires the MCP client to live on the same machine as the server, because
the server talks to the in-game bridge over `localhost:15526`. When the client runs
elsewhere — a Glimmung/tank session pod on the tailnet driving a run on the game
host — start the server in HTTP mode **on the game host** instead and point the
remote client at it. No port-forward needed.

On the game host, bind to its Tailscale IP:

```powershell
uv run --directory D:\repos\spire-lens-mcp\mcp python server.py `
  --transport http --bind-host <host-tailscale-ip> --bind-port 15527
```

The server still reaches the game over the local `--host`/`--port` bridge, so it
survives game restarts and reconnects on its own. `--bind-host` defaults to
loopback so `--transport http` is never accidentally world-reachable; set it to
the Tailscale IP to accept remote clients. Keep `--bind-port` (MCP, default
`15527`) distinct from `--port` (the in-game bridge, `15526`).

Point the remote client's `.mcp.json` at it:

```json
{
  "mcpServers": {
    "sts2-modding": {
      "type": "http",
      "url": "http://<host-tailscale-ip>:15527/mcp"
    }
  }
}
```

Access control is layered. Pick a mode with `--auth-mode` (default: `token` when
`--auth-token` / `$SPIRELENS_MCP_TOKEN` is set, otherwise `none`):

- **`none`** — rely on the network layer alone: bind to the Tailscale interface
  and let tailnet ACLs against `tag:spirelens-host` gate access.
- **`token`** — shared-secret gate. Set `--auth-token <token>` (or
  `$SPIRELENS_MCP_TOKEN`); clients send `Authorization: Bearer <token>` — add
  `"headers": {"Authorization": "Bearer <token>"}` to the `.mcp.json` entry.
- **`jwt`** — validate an [`auth.romaine.life`](https://auth.romaine.life) RS256
  service token against its JWKS. **This is the mode the SpireLens deployment
  uses:** a tank/glimmung session pod presents its projected `auth.romaine.life`
  token, so **no shared secret is distributed**. The token is checked for
  signature (via JWKS), issuer, expiry, and `role` (default `service`); an
  optional `--auth-allowed-actor-emails` CSV restricts which human owners may
  drive the host. Defaults (each also reads a `SPIRELENS_MCP_AUTH_*` env var):
  `--auth-jwks-url https://auth.romaine.life/api/auth/jwks`,
  `--auth-issuer https://auth.romaine.life`, `--auth-required-role service`.
  Clients do **not** put the token in `.mcp.json`; the in-cluster MCP auth proxy
  injects the `Authorization` header on the pod's behalf (see the SpireLens /
  tank-operator tailnet-host-access docs).

## License

MIT — see [`LICENSE`](LICENSE).

The original copyright notice (Yikun Ji / Kunologist, 2026) is preserved in `LICENSE` per the MIT terms. Modifications by nelsong6 are documented in `AUTHORS.md` and the git history of this repo.
