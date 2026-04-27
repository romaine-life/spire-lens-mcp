"""MCP server bridge for Slay the Spire 2.

Connects to the SpireLensMcp mod's HTTP server and exposes game actions
as MCP tools for Claude Desktop / Claude Code.
"""

import argparse
import asyncio
import base64
import copy
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

import httpx
from mcp.server.fastmcp import FastMCP

mcp = FastMCP("sts2")

_base_url: str = "http://localhost:15526"
_trust_env: bool = True


def _sp_url() -> str:
    return f"{_base_url}/api/v1/singleplayer"


def _mp_url() -> str:
    return f"{_base_url}/api/v1/multiplayer"


def _catalog_url() -> str:
    return f"{_base_url}/api/v1/catalog"


def _screenshot_url() -> str:
    return f"{_base_url}/api/v1/screenshot"


async def _get(params: dict | None = None) -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.get(_sp_url(), params=params)
        r.raise_for_status()
        return r.text


async def _post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.post(_sp_url(), json=body)
        r.raise_for_status()
        return r.text


async def _mp_get(params: dict | None = None) -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.get(_mp_url(), params=params)
        r.raise_for_status()
        return r.text


async def _mp_post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.post(_mp_url(), json=body)
        r.raise_for_status()
        return r.text


async def _catalog_get() -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.get(_catalog_url())
        r.raise_for_status()
        return r.text


async def _catalog_post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10, trust_env=_trust_env) as client:
        r = await client.post(_catalog_url(), json=body)
        r.raise_for_status()
        return r.text


async def _screenshot_get() -> dict:
    async with httpx.AsyncClient(timeout=20, trust_env=_trust_env) as client:
        r = await client.get(_screenshot_url())
        r.raise_for_status()
        return r.json()




def _safe_screenshot_name(name: str | None) -> str:
    value = (name or "sts2-screenshot").strip()
    if not value:
        value = "sts2-screenshot"
    value = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip(".-")
    if not value:
        value = "sts2-screenshot"
    if not value.lower().endswith(".png"):
        value += ".png"
    return value


def _screenshot_dir() -> Path:
    configured = os.environ.get("SCREENSHOT_DIR")
    if configured and configured.strip():
        return Path(configured)
    return Path(tempfile.gettempdir()) / "sts2-screenshots"


def _user_data_dir() -> Path:
    configured = os.environ.get("STS2_USER_DATA_DIR")
    if configured and configured.strip():
        return Path(configured)
    appdata = os.environ.get("APPDATA")
    if appdata and appdata.strip():
        return Path(appdata) / "SlayTheSpire2"
    return Path.home() / "AppData" / "Roaming" / "SlayTheSpire2"


def _base_save_dir() -> Path:
    configured = os.environ.get("STS2_BASE_SAVE_DIR")
    if configured and configured.strip():
        return Path(configured)
    return _user_data_dir() / "SpireLensMcp" / "base_saves"


def _scenario_save_dir() -> Path:
    configured = os.environ.get("STS2_SCENARIO_SAVE_DIR")
    if configured and configured.strip():
        return Path(configured)
    return _user_data_dir() / "SpireLensMcp" / "scenario_saves"


def _current_run_save_path() -> Path:
    configured = os.environ.get("STS2_CURRENT_RUN_SAVE")
    if configured and configured.strip():
        return Path(configured)
    candidates = sorted(
        _user_data_dir().glob("steam/*/modded/profile1/saves/current_run.save"),
        key=lambda p: p.stat().st_mtime if p.exists() else 0,
        reverse=True,
    )
    if candidates:
        return candidates[0]
    return _user_data_dir() / "steam" / "76561198062015438" / "modded" / "profile1" / "saves" / "current_run.save"


def _steam_remote_current_run_save_path() -> Path | None:
    configured = os.environ.get("STS2_STEAM_REMOTE_CURRENT_RUN_SAVE")
    if configured and configured.strip():
        return Path(configured)

    appdata_current = _current_run_save_path()
    marker = ("modded", "profile1", "saves", "current_run.save")
    parts = appdata_current.parts
    suffix_start = None
    for i in range(0, len(parts) - len(marker) + 1):
        if tuple(parts[i : i + len(marker)]) == marker:
            suffix_start = i
            break
    suffix = Path(*parts[suffix_start:]) if suffix_start is not None else Path(*marker)

    candidates: list[Path] = []
    for root in [
        Path(os.environ.get("ProgramFiles(x86)", "")) / "Steam" / "userdata",
        Path(os.environ.get("ProgramFiles", "")) / "Steam" / "userdata",
    ]:
        if root.exists():
            candidates.extend(root.glob(f"*/2868840/remote/{suffix.as_posix()}"))
            candidates.extend(remote / suffix for remote in root.glob("*/2868840/remote") if remote.is_dir())

    existing = [p for p in candidates if p.exists()]
    if existing:
        return max(existing, key=lambda p: p.stat().st_mtime)
    return candidates[0] if candidates else None


def _current_run_save_targets() -> list[Path]:
    targets = [_current_run_save_path()]
    remote = _steam_remote_current_run_save_path()
    if remote and remote not in targets:
        targets.append(remote)
    return targets


def _safe_save_name(name: str) -> str:
    value = (name or "").strip()
    value = re.sub(r"[^A-Za-z0-9._-]+", "_", value).strip("._-")
    if not value:
        raise ValueError("save name is required")
    if value.lower().endswith(".save"):
        value = value[:-5]
    return value


def _named_save_path(root: Path, name: str) -> Path:
    return root / f"{_safe_save_name(name)}.save"


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _load_save_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError("save root must be a JSON object")
    return data


def _write_save_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def _normalize_card_id(card_id: str) -> str:
    value = card_id.strip().upper()
    return value if value.startswith("CARD.") else f"CARD.{value}"


def _normalize_relic_id(relic_id: str) -> str:
    value = relic_id.strip().upper()
    return value if value.startswith("RELIC.") else f"RELIC.{value}"


def _normalize_encounter_id(encounter_id: str) -> str:
    value = encounter_id.strip().upper()
    return value if value.startswith("ENCOUNTER.") else f"ENCOUNTER.{value}"


def _card_entry(card_id: str, floor_added_to_deck: int = 1) -> dict:
    return {"floor_added_to_deck": floor_added_to_deck, "id": _normalize_card_id(card_id)}


def _relic_entry(relic_id: str, floor_added_to_deck: int = 1) -> dict:
    return {"floor_added_to_deck": floor_added_to_deck, "id": _normalize_relic_id(relic_id)}


def _save_summary(data: dict) -> dict:
    players = data.get("players")
    player = players[0] if isinstance(players, list) and players and isinstance(players[0], dict) else {}
    deck = player.get("deck") if isinstance(player.get("deck"), list) else []
    relics = player.get("relics") if isinstance(player.get("relics"), list) else []
    return {
        "schema_version": data.get("schema_version"),
        "character_id": player.get("character_id"),
        "ascension": data.get("ascension"),
        "current_act_index": data.get("current_act_index"),
        "floor_reached": len(data.get("visited_map_coords") or []),
        "gold": player.get("gold"),
        "current_hp": player.get("current_hp"),
        "max_hp": player.get("max_hp"),
        "max_energy": player.get("max_energy"),
        "deck_count": len(deck),
        "deck": [card.get("id") for card in deck if isinstance(card, dict)],
        "relic_count": len(relics),
        "relics": [relic.get("id") for relic in relics if isinstance(relic, dict)],
        "pre_finished_room": data.get("pre_finished_room"),
    }


def _save_viewport_png(output_path: Path, metadata: dict) -> dict:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    png_base64 = metadata.get("png_base64")
    if not isinstance(png_base64, str) or not png_base64:
        raise RuntimeError("screenshot response did not include png_base64")

    output_path.write_bytes(base64.b64decode(png_base64))

    clean = {key: value for key, value in metadata.items() if key != "png_base64"}
    clean["path"] = str(output_path)
    return clean


def _handle_error(e: Exception) -> str:
    if isinstance(e, httpx.ConnectError):
        return "Error: Cannot connect to SpireLensMcp mod. Is the game running with the mod enabled?"
    if isinstance(e, httpx.HTTPStatusError):
        return f"Error: HTTP {e.response.status_code} — {e.response.text}"
    return f"Error: {e}"


# ---------------------------------------------------------------------------
# General
# ---------------------------------------------------------------------------
@mcp.tool()
async def lookup_card(query: str, max_matches: int = 10) -> str:
    """Look up card identity and ownership from the live STS2 model catalog.

    Use this instead of model memory whenever an issue names a card. The result
    is structured JSON with status `ok`, `not_found`, or `ambiguous`; abort the
    investigation on `not_found` or `ambiguous` instead of guessing.

    Args:
        query: Card id, display name, or partial name from the issue.
        max_matches: Maximum ambiguous matches to return.
    """
    try:
        return await _catalog_post({"action": "lookup_card", "query": query, "max_matches": max_matches})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def list_cards(
    owner: str | None = None,
    type: str | None = None,
    query: str | None = None,
    limit: int = 50,
) -> str:
    """List cards from the live STS2 model catalog with optional filters.

    Use this when choosing fixture cards. For example, ask for Regent Skill
    cards instead of guessing ids from model memory.

    Args:
        owner: Optional character id/name/card-pool id, such as "REGENT".
        type: Optional card type, such as "Skill", "Attack", or "Power".
        query: Optional id/name substring filter.
        limit: Maximum cards to return.
    """
    try:
        return await _catalog_post({
            "action": "list_cards",
            "owner": owner or "",
            "type": type or "",
            "query": query or "",
            "limit": limit,
        })
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def lookup_character(query: str) -> str:
    """Look up character identity from the live STS2 model catalog.

    Use this instead of model memory whenever an issue names a character. The
    result is structured JSON with status `ok`, `not_found`, or `ambiguous`.

    Args:
        query: Character id, display name, or partial name from the issue.
    """
    try:
        return await _catalog_post({"action": "lookup_character", "query": query})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def list_characters() -> str:
    """List registered STS2 characters and their card-pool identifiers."""
    try:
        return await _catalog_post({"action": "list_characters"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_catalog_summary() -> str:
    """Get a compact summary of the live STS2 model catalog."""
    try:
        return await _catalog_get()
    except Exception as e:
        return _handle_error(e)
@mcp.tool()
async def capture_screenshot(name: str | None = None) -> str:
    """Capture the full Slay the Spire 2 game viewport.

    This is the canonical screenshot path for live validation. It captures the
    in-game Godot root viewport via the SpireLens MCP mod and writes a PNG under
    SCREENSHOT_DIR. Use this for evidence whenever a live run, combat, tooltip,
    or UI state is being validated.

    Args:
        name: Optional PNG file name. Unsafe characters are replaced with dashes.
    """
    try:
        output_path = _screenshot_dir() / _safe_screenshot_name(name)
        metadata = await _screenshot_get()
        return json.dumps(_save_viewport_png(output_path, metadata), indent=2)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def list_save_files(kind: str = "base") -> str:
    """List materialized STS2 save files managed by the MCP bridge.

    Args:
        kind: "base" for reusable character bases or "scenario" for derived test saves.
    """
    try:
        root = _base_save_dir() if kind == "base" else _scenario_save_dir()
        saves = []
        if root.exists():
            for path in sorted(root.glob("*.save"), key=lambda p: p.name.lower()):
                saves.append({
                    "name": path.stem,
                    "path": str(path),
                    "bytes": path.stat().st_size,
                    "sha256": _sha256(path),
                    "updated_at": path.stat().st_mtime,
                })
        return json.dumps({"status": "ok", "kind": kind, "directory": str(root), "count": len(saves), "saves": saves}, indent=2)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def inspect_save(name: str, kind: str = "base") -> str:
    """Inspect a managed STS2 save and return a compact structured summary.

    Args:
        name: Save name without extension, such as "base_ironclad".
        kind: "base" or "scenario".
    """
    try:
        root = _base_save_dir() if kind == "base" else _scenario_save_dir()
        path = _named_save_path(root, name)
        data = _load_save_json(path)
        return json.dumps({
            "status": "ok",
            "kind": kind,
            "name": _safe_save_name(name),
            "path": str(path),
            "bytes": path.stat().st_size,
            "sha256": _sha256(path),
            "summary": _save_summary(data),
        }, indent=2)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def materialize_scenario_save(
    base_name: str,
    scenario_name: str,
    deck: list[str] | None = None,
    add_cards: list[str] | None = None,
    remove_cards: list[str] | None = None,
    relics: list[str] | None = None,
    add_relics: list[str] | None = None,
    remove_relics: list[str] | None = None,
    gold: int | None = None,
    current_hp: int | None = None,
    max_hp: int | None = None,
    max_energy: int | None = None,
    next_normal_encounter: str | None = None,
) -> str:
    """Create a derived scenario save by editing stable JSON save fields.

    This intentionally edits only pre-combat run fields that are easy to audit:
    deck, relics, gold, HP, and max energy. Combat piles/turn state are out of
    scope for direct save editing until separately proven.

    Args:
        base_name: Base save name, such as "base_ironclad".
        scenario_name: Output scenario save name.
        deck: Optional complete replacement deck, as card ids without/with CARD.
        add_cards: Optional card ids to append to the deck.
        remove_cards: Optional card ids to remove from the deck, one copy per id.
        relics: Optional complete replacement relic list, as ids without/with RELIC.
        add_relics: Optional relic ids to append.
        remove_relics: Optional relic ids to remove, one copy per id.
        gold: Optional exact gold.
        current_hp: Optional exact current HP.
        max_hp: Optional exact max HP.
        max_energy: Optional exact max energy.
        next_normal_encounter: Optional encounter id to place at the next normal
            encounter slot, e.g. FUZZY_WURM_CRAWLER_WEAK.
    """
    try:
        base_path = _named_save_path(_base_save_dir(), base_name)
        output_path = _named_save_path(_scenario_save_dir(), scenario_name)
        data = _load_save_json(base_path)
        edited = copy.deepcopy(data)

        players = edited.get("players")
        if not isinstance(players, list) or not players or not isinstance(players[0], dict):
            raise ValueError("save does not contain players[0]")
        player = players[0]

        if deck is not None:
            player["deck"] = [_card_entry(card) for card in deck]
        else:
            current_deck = player.get("deck")
            if not isinstance(current_deck, list):
                current_deck = []
            current_deck = [card for card in current_deck if isinstance(card, dict)]
            for card_id in remove_cards or []:
                target = _normalize_card_id(card_id)
                for i, card in enumerate(current_deck):
                    if card.get("id") == target:
                        current_deck.pop(i)
                        break
            current_deck.extend(_card_entry(card) for card in add_cards or [])
            player["deck"] = current_deck

        if relics is not None:
            player["relics"] = [_relic_entry(relic) for relic in relics]
        else:
            current_relics = player.get("relics")
            if not isinstance(current_relics, list):
                current_relics = []
            current_relics = [relic for relic in current_relics if isinstance(relic, dict)]
            for relic_id in remove_relics or []:
                target = _normalize_relic_id(relic_id)
                for i, relic in enumerate(current_relics):
                    if relic.get("id") == target:
                        current_relics.pop(i)
                        break
            current_relics.extend(_relic_entry(relic) for relic in add_relics or [])
            player["relics"] = current_relics

        if gold is not None:
            player["gold"] = gold
        if current_hp is not None:
            player["current_hp"] = current_hp
        if max_hp is not None:
            player["max_hp"] = max_hp
        if max_energy is not None:
            player["max_energy"] = max_energy

        if next_normal_encounter is not None:
            acts = edited.get("acts")
            if not isinstance(acts, list) or not acts or not isinstance(acts[0], dict):
                raise ValueError("save does not contain acts[0]")
            rooms = acts[0].get("rooms")
            if not isinstance(rooms, dict):
                raise ValueError("save does not contain acts[0].rooms")
            normal_ids = rooms.get("normal_encounter_ids")
            if not isinstance(normal_ids, list) or not normal_ids:
                raise ValueError("save does not contain normal_encounter_ids")
            visited = rooms.get("normal_encounters_visited")
            index = visited if isinstance(visited, int) else 0
            index = max(0, min(index, len(normal_ids) - 1))
            normal_ids[index] = _normalize_encounter_id(next_normal_encounter)

        _write_save_json(output_path, edited)
        return json.dumps({
            "status": "ok",
            "base": str(base_path),
            "scenario": str(output_path),
            "scenario_name": _safe_save_name(scenario_name),
            "bytes": output_path.stat().st_size,
            "sha256": _sha256(output_path),
            "before": _save_summary(data),
            "after": _save_summary(edited),
            "next_step": "Load this scenario save in-game and verify with get_game_state before using it in an agent test.",
        }, indent=2)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def install_save_as_current(name: str, kind: str = "scenario") -> str:
    """Install a managed base/scenario save as STS2's current_run.save.

    A timestamped backup of the previous current run save is written next to it.
    On Steam builds this writes both the AppData working save and the Steam
    userdata remote mirror, because STS2 syncs the remote mirror back over
    AppData on launch when the two disagree. Use this while STS2 is closed.

    Args:
        name: Managed save name without extension.
        kind: "scenario" or "base".
    """
    try:
        source_root = _base_save_dir() if kind == "base" else _scenario_save_dir()
        source = _named_save_path(source_root, name)
        if not source.exists():
            raise FileNotFoundError(source)

        bytes_to_install = source.read_bytes()
        installed = []
        for current in _current_run_save_targets():
            current.parent.mkdir(parents=True, exist_ok=True)
            companion = current.with_name(f"{current.name}.backup")
            backup = None
            if current.exists():
                backup = current.with_name(f"{current.stem}.backup-{int(__import__('time').time())}{current.suffix}")
                backup.write_bytes(current.read_bytes())
            current.write_bytes(bytes_to_install)
            companion.write_bytes(bytes_to_install)
            installed.append({
                "current_run_save": str(current),
                "current_run_backup": str(companion),
                "previous_backup": str(backup) if backup else None,
                "bytes": current.stat().st_size,
                "sha256": _sha256(current),
            })

        return json.dumps({
            "status": "ok",
            "installed": str(source),
            "targets": installed,
            "target_count": len(installed),
            "sha256": _sha256(source),
            "next_step": "Launch STS2 after installing, then load the run and inspect get_game_state.",
        }, indent=2)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def validate_current_run_save() -> str:
    """Ask the in-game MCP mod to deserialize STS2's current_run.save.

    This validates with the game's own SaveManager rather than only Python JSON
    parsing. It does not load the run into the scene.
    """
    try:
        return await _post({"action": "dev_validate_current_run_save"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def load_current_run_save() -> str:
    """Load STS2's current_run.save through the game's saved-run path.

    Use after `install_save_as_current` while no run is in progress. This
    bypasses menu UI and directly performs the lower-level saved-run sequence:
    deserialize save, create RunState, set up saved singleplayer, and load run.
    """
    try:
        return await _post({"action": "dev_load_current_run_save"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_game_state(format: str = "markdown") -> str:
    """Get the current Slay the Spire 2 game state.

    Returns the full game state including player stats, hand, enemies, potions, etc.
    The state_type field indicates the current screen (combat, map, event, shop,
    fake_merchant, etc.).

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
    """
    try:
        return await _get({"format": format})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def use_potion(slot: int, target: str | None = None) -> str:
    """Use a potion from the player's potion slots.

    Works both during and outside of combat. Combat-only potions require an active battle.

    Args:
        slot: Potion slot index (as shown in game state).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def discard_potion(slot: int) -> str:
    """Discard a potion from the player's potion slots to free up space.

    Use this when all potion slots are full and you need room for incoming potions
    (e.g. before collecting a potion reward).

    Args:
        slot: Potion slot index to discard (as shown in game state).
    """
    try:
        return await _post({"action": "discard_potion", "slot": slot})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def proceed_to_map() -> str:
    """Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop, fake merchant.
    Does NOT work for events — use event_choose_option() with the Proceed option's index.
    """
    try:
        return await _post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Dev validation
# ---------------------------------------------------------------------------


@mcp.tool()
async def reload_spirelens_core() -> str:
    """Reload the SpireLens hot-reloadable Core assembly through the in-game bridge.

    Use after building/deploying SpireLens.Core.dll and before screenshotting changed
    SpireLens behavior. This invokes the same loader reload path as the in-game F5
    hotkey, when SpireLens is loaded.
    """
    try:
        return await _post({"action": "dev_reload_spirelens_core"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def start_singleplayer_run(character: str = "Ironclad", ascension: int = 0, seed: str | None = None) -> str:
    """Start a dev singleplayer run without clicking through the main menu.

    Args:
        character: Character id or display name, such as "Ironclad", "Silent", "Regent", "Necrobinder", or "Defect".
        ascension: Ascension level to start.
        seed: Optional run seed. If omitted, the game generates one.
    """
    body: dict = {
        "action": "dev_start_singleplayer_run",
        "character": character,
        "ascension": ascension,
    }
    if seed is not None:
        body["seed"] = seed
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def enter_debug_room(room_type: str = "monster") -> str:
    """Move the current run into a debug room for live validation setup.

    Args:
        room_type: STS2 RoomType value, such as "Monster", "Elite", "Boss", "Treasure", "Shop", "Event", or "RestSite".
    """
    try:
        return await _post({"action": "dev_enter_room", "room_type": room_type})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def list_scenario_commands() -> str:
    """List guarded STS2 dev-console commands available for scenario setup.

    These commands use the game's own dev-console pathways and are safer for
    scenario setup than direct model/pile mutation.
    """
    try:
        return await _post({"action": "dev_list_scenario_commands"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def run_scenario_command(command: str) -> str:
    """Run one allowlisted STS2 dev-console command for scenario setup.

    Use this as the scenario-builder substrate. The command is routed through
    the game's DevConsole.ProcessCommand path, with only scenario-safe commands
    exposed by the MCP mod. Dangerous commands such as cloud/delete/open/sentry
    are not accepted.

    Args:
        command: A dev-console command such as "card MAKE_IT_SO discard",
            "card DEFEND_REGENT", "draw 3", "fight JAW_WORM", "energy 4",
            or "stars 3".
    """
    try:
        return await _post({"action": "dev_run_scenario_command", "command": command})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def configure_run_deck(deck: list[str]) -> str:
    """Replace the current run deck before combat starts.

    Use this before entering a test combat. Combat initialization registers
    playable combat cards from the run deck; changing the deck after combat has
    started can produce cards that appear in piles but cannot be played.

    Args:
        deck: Card ids or exact names to make the permanent deck for this scenario.
    """
    try:
        return await _post({"action": "dev_configure_run_deck", "deck": deck})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def replace_run_deck_and_save(
    deck: list[str],
    update_combat_piles: bool = True,
    persist: bool = True,
) -> str:
    """Replace the live run deck in-game and optionally persist it.

    Unlike offline save-file editing, this mutates the currently loaded
    RunState and asks the game to write the save through SaveManager. In combat,
    `update_combat_piles` also replaces hand/draw/discard/exhaust so the visible
    state matches the permanent deck immediately.

    Args:
        deck: Card ids or exact names to make the permanent deck.
        update_combat_piles: If in combat, also replace visible combat piles.
        persist: If true, call the game's own SaveManager.SaveRun.
    """
    try:
        return await _post({
            "action": "dev_replace_run_deck_and_save",
            "deck": deck,
            "update_combat_piles": update_combat_piles,
            "persist": persist,
        })
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def configure_test_combat(
    deck: list[str] | None = None,
    hand: list[str] | None = None,
    draw_pile: list[str] | None = None,
    discard_pile: list[str] | None = None,
    exhaust_pile: list[str] | None = None,
    enemy_hp: int = 999,
    energy: int | None = None,
    stars: int | None = None,
    player_powers: list[dict] | None = None,
    enemy_powers: list[dict] | None = None,
) -> str:
    """Configure the current combat into a deterministic validation fixture.

    Default validation should use a simple early/debug monster with high HP and
    controlled card piles. Use `configure_run_deck` before entering combat, then
    use this after `enter_debug_room("Monster")` to move already-registered
    combat cards into hand/draw/discard/exhaust. If an issue needs kills, enemy
    powers/statuses, multiple enemies, or special enemy behavior, the
    investigation phase should say why the default fixture is insufficient.

    Args:
        deck: Deprecated. Configure the deck before combat with `configure_run_deck`.
        hand: Card ids or exact names to put in hand, left to right.
        draw_pile: Card ids or exact names to put in draw pile.
        discard_pile: Card ids or exact names to put in discard pile.
        exhaust_pile: Card ids or exact names to put in exhaust pile.
        enemy_hp: HP to set on all living enemies. Defaults to 999.
        energy: Optional exact current player energy.
        stars: Optional exact current Regent stars.
        player_powers: Optional list like [{"power": "Artifact", "amount": 1}].
        enemy_powers: Optional list like [{"power": "Poison", "amount": 5, "target_index": 0}].
    """
    try:
        body: dict = {
            "action": "dev_configure_test_combat",
            "deck": [],
            "hand": hand or [],
            "draw_pile": draw_pile or [],
            "discard_pile": discard_pile or [],
            "exhaust_pile": exhaust_pile or [],
            "enemy_hp": enemy_hp,
            "player_powers": player_powers or [],
            "enemy_powers": enemy_powers or [],
        }
        if energy is not None:
            body["energy"] = energy
        if stars is not None:
            body["stars"] = stars
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


async def _wait_for_state(predicate, timeout_seconds: int) -> dict:
    deadline = asyncio.get_running_loop().time() + timeout_seconds
    last_state: dict = {}
    while asyncio.get_running_loop().time() < deadline:
        state_text = await _get({"format": "json"})
        last_state = json.loads(state_text)
        if predicate(last_state):
            return last_state
        await asyncio.sleep(0.5)
    raise TimeoutError(f"Timed out waiting for requested game state. Last state: {last_state}")


# ---------------------------------------------------------------------------
# Combat (state_type: monster / elite / boss)
# ---------------------------------------------------------------------------


@mcp.tool()
async def combat_play_card(card_index: int, target: str | None = None) -> str:
    """[Combat] Play a card from the player's hand.

    Args:
        card_index: Index of the card in hand (0-based, as shown in game state).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for single-target cards.

    Note that the index can change as cards are played - playing a card will shift the indices of remaining cards in hand.
    Refer to the latest game state for accurate indices. New cards are drawn to the right, so playing cards from right to left can help maintain more stable indices for remaining cards.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def combat_end_turn() -> str:
    """[Combat] End the player's current turn."""
    try:
        return await _post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# In-Combat Card Selection (state_type: hand_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def combat_select_card(card_index: int) -> str:
    """[Combat Selection] Select a card from hand during an in-combat card selection prompt.

    Used when a card effect asks you to select a card to exhaust, discard, etc.
    This is different from deck_select_card which handles out-of-combat card selection overlays.

    Args:
        card_index: 0-based index of the card in the selectable hand cards (as shown in game state).
    """
    try:
        return await _post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def combat_confirm_selection() -> str:
    """[Combat Selection] Confirm the in-combat card selection.

    After selecting the required number of cards from hand (exhaust, discard, etc.),
    use this to confirm the selection. Only works when the confirm button is enabled.
    """
    try:
        return await _post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rewards (state_type: rewards / card_reward)
# ---------------------------------------------------------------------------


@mcp.tool()
async def rewards_claim(reward_index: int) -> str:
    """[Rewards] Claim a reward from the post-combat rewards screen.

    Gold, potion, and relic rewards are claimed immediately.
    Card rewards open the card selection screen (state changes to card_reward).

    Args:
        reward_index: 0-based index of the reward on the rewards screen.

    Note that claiming a reward may change the indices of remaining rewards, so refer to the latest game state for accurate indices.
    Claiming from right to left can help maintain more stable indices for remaining rewards, as rewards will always shift left to fill in gaps.
    """
    try:
        return await _post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def rewards_pick_card(card_index: int) -> str:
    """[Rewards] Select a card from the card reward selection screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def rewards_skip_card() -> str:
    """[Rewards] Skip the card reward without selecting a card."""
    try:
        return await _post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Map (state_type: map)
# ---------------------------------------------------------------------------


@mcp.tool()
async def map_choose_node(node_index: int) -> str:
    """[Map] Choose a map node to travel to.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rest Site (state_type: rest_site)
# ---------------------------------------------------------------------------


@mcp.tool()
async def rest_choose_option(option_index: int) -> str:
    """[Rest Site] Choose a rest site option (rest, smith, etc.).

    Args:
        option_index: 0-based index of the option from the rest site state.
    """
    try:
        return await _post({"action": "choose_rest_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Shop (state_type: shop)
# ---------------------------------------------------------------------------


@mcp.tool()
async def shop_purchase(item_index: int) -> str:
    """[Shop / Fake Merchant] Purchase an item from the shop.

    Works for both regular shops (state_type: shop) and the fake merchant
    event (state_type: fake_merchant). The fake merchant only sells relics.

    Args:
        item_index: 0-based index of the item from the shop state.
    """
    try:
        return await _post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Event (state_type: event)
# ---------------------------------------------------------------------------


@mcp.tool()
async def event_choose_option(option_index: int) -> str:
    """[Event] Choose an event option.

    Works for both regular events and ancients (after dialogue ends).
    Also used to click the Proceed option after an event resolves.

    Args:
        option_index: 0-based index of the option from the event state.
    """
    try:
        return await _post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def event_advance_dialogue() -> str:
    """[Event] Advance ancient event dialogue.

    Click through dialogue text in ancient events. Call repeatedly until options appear.
    """
    try:
        return await _post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Card Selection (state_type: card_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def deck_select_card(card_index: int) -> str:
    """[Card Selection] Select or deselect a card in the card selection screen.

    Used when the game asks you to choose cards from your deck (transform, upgrade,
    remove, discard) or pick a card from offered choices (potions, effects).

    For deck selections: toggles card selection. For choose-a-card: picks immediately.

    Args:
        card_index: 0-based index of the card (as shown in game state).
    """
    try:
        return await _post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def deck_confirm_selection() -> str:
    """[Card Selection] Confirm the current card selection.

    After selecting the required number of cards, use this to confirm.
    If a preview is showing (e.g., transform preview), this confirms the preview.
    Not needed for choose-a-card screens where picking is immediate.
    """
    try:
        return await _post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def deck_cancel_selection() -> str:
    """[Card Selection] Cancel the current card selection.

    If a preview is showing, goes back to the selection grid.
    For choose-a-card screens, clicks the skip button (if available).
    Otherwise, closes the card selection screen (only if cancellation is allowed).
    """
    try:
        return await _post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Bundle Selection (state_type: bundle_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def bundle_select(bundle_index: int) -> str:
    """[Bundle Selection] Open a bundle preview.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def bundle_confirm_selection() -> str:
    """[Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def bundle_cancel_selection() -> str:
    """[Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Relic Selection (state_type: relic_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def relic_select(relic_index: int) -> str:
    """[Relic Selection] Select a relic from the relic selection screen.

    Used when the game offers a choice of relics (e.g., boss relic rewards).

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def relic_skip() -> str:
    """[Relic Selection] Skip the relic selection without choosing a relic."""
    try:
        return await _post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Treasure (state_type: treasure)
# ---------------------------------------------------------------------------


@mcp.tool()
async def treasure_claim_relic(relic_index: int) -> str:
    """[Treasure] Claim a relic from the treasure chest.

    The chest is auto-opened when entering the treasure room.
    After claiming, use proceed_to_map() to continue.

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Crystal Sphere (state_type: crystal_sphere)
# ---------------------------------------------------------------------------


@mcp.tool()
async def crystal_sphere_set_tool(tool: str) -> str:
    """[Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def crystal_sphere_proceed() -> str:
    """[Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


# ===========================================================================
# MULTIPLAYER tools — all route through /api/v1/multiplayer
# ===========================================================================


@mcp.tool()
async def mp_get_game_state(format: str = "markdown") -> str:
    """[Multiplayer] Get the current multiplayer game state.

    Returns a summary of all players (HP, gold, alive status) plus full
    detail for the local player (relics, potions, deck, etc.), along with
    multiplayer-specific data: map votes, event votes, treasure bids,
    end-turn ready status. Only works during a multiplayer run.

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
    """
    try:
        return await _mp_get({"format": format})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_play_card(card_index: int, target: str | None = None) -> str:
    """[Multiplayer Combat] Play a card from the local player's hand.

    Same as singleplayer combat_play_card but routed through the multiplayer
    endpoint for sync safety.

    Args:
        card_index: Index of the card in hand (0-based).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for single-target cards.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_end_turn() -> str:
    """[Multiplayer Combat] Submit end-turn vote.

    In multiplayer, ending the turn is a VOTE — the turn only ends when ALL
    players have submitted. Use mp_combat_undo_end_turn() to retract.
    """
    try:
        return await _mp_post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_undo_end_turn() -> str:
    """[Multiplayer Combat] Retract end-turn vote.

    If you submitted end turn but want to play more cards, use this to undo.
    Only works if other players haven't all committed yet.
    """
    try:
        return await _mp_post({"action": "undo_end_turn"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_use_potion(slot: int, target: str | None = None) -> str:
    """[Multiplayer] Use a potion from the local player's potion slots.

    Args:
        slot: Potion slot index (as shown in game state).
        target: Entity ID of the target enemy. Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_discard_potion(slot: int) -> str:
    """[Multiplayer] Discard a potion from the local player's potion slots to free up space.

    Args:
        slot: Potion slot index to discard (as shown in game state).
    """
    try:
        return await _mp_post({"action": "discard_potion", "slot": slot})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_map_vote(node_index: int) -> str:
    """[Multiplayer Map] Vote for a map node to travel to.

    In multiplayer, map selection is a vote — travel happens when all players
    agree. Re-voting for the same node sends a ping to other players.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _mp_post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_event_choose_option(option_index: int) -> str:
    """[Multiplayer Event] Choose or vote for an event option.

    For shared events: this is a vote (resolves when all players vote).
    For individual events: immediate choice, same as singleplayer.

    Args:
        option_index: 0-based index of the option from the event state.
    """
    try:
        return await _mp_post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_event_advance_dialogue() -> str:
    """[Multiplayer Event] Advance ancient event dialogue."""
    try:
        return await _mp_post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rest_choose_option(option_index: int) -> str:
    """[Multiplayer Rest Site] Choose a rest site option (rest, smith, etc.).

    Per-player choice — no voting needed.

    Args:
        option_index: 0-based index of the option.
    """
    try:
        return await _mp_post({"action": "choose_rest_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_shop_purchase(item_index: int) -> str:
    """[Multiplayer Shop] Purchase an item from the shop.

    Per-player inventory — no voting needed.

    Args:
        item_index: 0-based index of the item.
    """
    try:
        return await _mp_post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_claim(reward_index: int) -> str:
    """[Multiplayer Rewards] Claim a reward from the post-combat rewards screen.

    Args:
        reward_index: 0-based index of the reward.
    """
    try:
        return await _mp_post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_pick_card(card_index: int) -> str:
    """[Multiplayer Rewards] Select a card from the card reward screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _mp_post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_skip_card() -> str:
    """[Multiplayer Rewards] Skip the card reward."""
    try:
        return await _mp_post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_proceed_to_map() -> str:
    """[Multiplayer] Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop.
    """
    try:
        return await _mp_post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_select_card(card_index: int) -> str:
    """[Multiplayer Card Selection] Select or deselect a card in the card selection screen.

    Args:
        card_index: 0-based index of the card.
    """
    try:
        return await _mp_post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_confirm_selection() -> str:
    """[Multiplayer Card Selection] Confirm the current card selection."""
    try:
        return await _mp_post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_cancel_selection() -> str:
    """[Multiplayer Card Selection] Cancel the current card selection."""
    try:
        return await _mp_post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_select(bundle_index: int) -> str:
    """[Multiplayer Bundle Selection] Open a bundle preview.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _mp_post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_confirm_selection() -> str:
    """[Multiplayer Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _mp_post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_cancel_selection() -> str:
    """[Multiplayer Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _mp_post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_select_card(card_index: int) -> str:
    """[Multiplayer Combat Selection] Select a card from hand during in-combat card selection.

    Args:
        card_index: 0-based index of the card in the selectable hand cards.
    """
    try:
        return await _mp_post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_confirm_selection() -> str:
    """[Multiplayer Combat Selection] Confirm the in-combat card selection."""
    try:
        return await _mp_post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_relic_select(relic_index: int) -> str:
    """[Multiplayer Relic Selection] Select a relic (boss relic rewards).

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_relic_skip() -> str:
    """[Multiplayer Relic Selection] Skip the relic selection."""
    try:
        return await _mp_post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_treasure_claim_relic(relic_index: int) -> str:
    """[Multiplayer Treasure] Bid on / claim a relic from the treasure chest.

    In multiplayer, this is a bid — if multiple players pick the same relic,
    a "relic fight" determines the winner. Others get consolation prizes.

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_set_tool(tool: str) -> str:
    """[Multiplayer Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _mp_post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Multiplayer Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _mp_post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_proceed() -> str:
    """[Multiplayer Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _mp_post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


def main():
    parser = argparse.ArgumentParser(description="SpireLens MCP Server")
    parser.add_argument("--port", type=int, default=15526, help="Game HTTP server port")
    parser.add_argument("--host", type=str, default="localhost", help="Game HTTP server host")
    parser.add_argument("--no-trust-env", action="store_true", help="Ignore HTTP_PROXY/HTTPS_PROXY environment variables")
    args = parser.parse_args()

    global _base_url, _trust_env
    _base_url = f"http://{args.host}:{args.port}"
    _trust_env = not args.no_trust_env

    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()

