using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpireLens.Mcp;

public static partial class McpMod
{
    private static readonly Lazy<DevConsole> ScenarioDevConsole = new(() => new DevConsole(shouldAllowDebugCommands: true));
    private static readonly HashSet<string> AllowedScenarioConsoleCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "card",
        "remove_card",
        "draw",
        "fight",
        "room",
        "energy",
        "stars",
        "power",
        "relic",
        "gold",
        "block",
        "heal",
        "kill",
        "win",
        "instant"
    };

    private static bool IsDevAction(string action) => action.StartsWith("dev_", StringComparison.Ordinal);

    private static Dictionary<string, object?> ExecuteDevAction(string action, Dictionary<string, JsonElement> data)
        => action switch
        {
            "dev_reload_spirelens_core" => ExecuteDevReloadSpireLensCore(),
            "dev_start_singleplayer_run" => ExecuteDevStartSingleplayerRun(data),
            "dev_list_scenario_commands" => ExecuteDevListScenarioCommands(),
            "dev_run_scenario_command" => ExecuteDevRunScenarioCommand(data),
            "dev_validate_current_run_save" => ExecuteDevValidateCurrentRunSave(),
            "dev_load_current_run_save" => ExecuteDevLoadCurrentRunSave(),
            "dev_configure_run_deck" => ExecuteDevConfigureRunDeck(data),
            "dev_replace_run_deck_and_save" => ExecuteDevReplaceRunDeckAndSave(data),
            "dev_enter_room" => ExecuteDevEnterRoom(data),
            "dev_configure_test_combat" => ExecuteDevConfigureTestCombat(data),
            "dev_set_spirelens_view_stats_enabled" => ExecuteDevSetSpireLensViewStatsEnabled(data),
            "dev_list_visible_cards" => ExecuteDevListVisibleCards(data),
            "dev_show_card_tooltip" => ExecuteDevShowCardTooltip(data),
            _ => Error($"Unknown dev action: {action}")
        };

    private static Dictionary<string, object?> ExecuteDevReloadSpireLensCore()
    {
        var loaderType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SpireLens.Loader.LoaderMain", throwOnError: false))
            .FirstOrDefault(t => t != null);

        if (loaderType == null)
            return Error("SpireLens loader was not found in the current AppDomain.");

        var reloadMethod = loaderType.GetMethod("ReloadCore", BindingFlags.Public | BindingFlags.Static);
        if (reloadMethod == null)
            return Error("SpireLens loader does not expose public static ReloadCore().");

        reloadMethod.Invoke(null, null);

        object? reloadNumber = null;
        var reloadNumberProperty = loaderType.GetProperty("ReloadNumber", BindingFlags.Public | BindingFlags.Static);
        if (reloadNumberProperty != null)
            reloadNumber = reloadNumberProperty.GetValue(null);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Requested SpireLens Core hot reload.",
            ["reload_number"] = reloadNumber
        };
    }

    private static Dictionary<string, object?> ExecuteDevSetSpireLensViewStatsEnabled(Dictionary<string, JsonElement> data)
    {
        bool enabled = GetBool(data, "enabled", true);
        var bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SpireLens.Loader.RuntimeOptionsBridge", throwOnError: false))
            .FirstOrDefault(t => t != null);

        if (bridgeType == null)
            return Error("SpireLens runtime options bridge was not found in the current AppDomain.");

        var setMethod = bridgeType.GetMethod("SetViewStatsToggleEnabled", BindingFlags.Public | BindingFlags.Static);
        if (setMethod == null)
            return Error("SpireLens runtime options bridge does not expose SetViewStatsToggleEnabled(bool).");

        setMethod.Invoke(null, new object[] { enabled });

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"SpireLens View Stats set to {enabled}.",
            ["enabled"] = enabled
        };
    }

    private static Dictionary<string, object?> ExecuteDevShowCardTooltip(Dictionary<string, JsonElement> data)
    {
        string surface = GetString(data, "surface", "hand").ToLowerInvariant();
        int index = GetInt(data, "card_index", 0);
        string cardId = GetString(data, "card_id", "");
        string cardNameQuery = GetString(data, "card_name", "");

        if (!TryResolveCardHolder(surface, index, cardId, cardNameQuery, out var holder, out var holders, out var resolvedIndex, out var error))
            return Error(error);

        var cardName = SafeGetText(() => holder!.CardModel?.Title) ?? "unknown";
        try
        {
            ClearVisibleCardHoverTips(holders);

            var createHoverTips = typeof(NCardHolder).GetMethod(
                "CreateHoverTips",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (createHoverTips == null)
                return Error("NCardHolder.CreateHoverTips could not be found.");

            createHoverTips.Invoke(holder, null);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException?.GetBaseException() ?? ex.GetBaseException();
            return Error($"NCardHolder tooltip invocation failed for {surface}[{resolvedIndex}] {cardName}: {inner.GetType().Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            return Error($"NCardHolder tooltip invocation failed for {surface}[{resolvedIndex}] {cardName}: {ex.GetBaseException().Message}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Showing hover tooltip for {surface}[{resolvedIndex}]: {cardName}.",
            ["surface"] = surface,
            ["card_index"] = resolvedIndex,
            ["card_id"] = holder!.CardModel?.Id.Entry,
            ["card_name"] = cardName,
            ["visible_cards"] = BuildVisibleCardList(holders)
        };
    }

    private static Dictionary<string, object?> ExecuteDevListVisibleCards(Dictionary<string, JsonElement> data)
    {
        string surface = GetString(data, "surface", "hand").ToLowerInvariant();
        if (!TryResolveCardHolders(surface, out var holders, out var error))
            return Error(error);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["surface"] = surface,
            ["count"] = holders.Count,
            ["cards"] = BuildVisibleCardList(holders)
        };
    }

    private static void ClearVisibleCardHoverTips(IEnumerable<NCardHolder> holders)
    {
        var clearHoverTips = typeof(NCardHolder).GetMethod(
            "ClearHoverTips",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (clearHoverTips == null)
            return;

        foreach (var candidate in holders)
        {
            try
            {
                if (GodotObject.IsInstanceValid(candidate))
                    clearHoverTips.Invoke(candidate, null);
            }
            catch
            {
                // Best effort: stale hover cleanup should not prevent showing the requested tooltip.
            }
        }
    }

    private static Dictionary<string, object?> ExecuteDevStartSingleplayerRun(Dictionary<string, JsonElement> data)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");
        if (NGame.Instance == null)
            return Error("NGame.Instance is not available yet.");

        string characterId = GetString(data, "character", "Ironclad");
        int ascension = GetInt(data, "ascension", 0);
        string seed = GetString(data, "seed", SeedHelper.GetRandomSeed());

        var character = ModelDb.AllCharacters.FirstOrDefault(c =>
            c.Id.Entry.Equals(characterId, StringComparison.OrdinalIgnoreCase)
            || (SafeGetText(() => c.Title)?.Equals(characterId, StringComparison.OrdinalIgnoreCase) ?? false));
        if (character == null)
            return Error($"Unknown character '{characterId}'.");

        var acts = ModelDb.Acts.ToList();
        if (acts.Count == 0)
            return Error("No acts are registered in ModelDb.");

        TaskHelper.RunSafely(NGame.Instance.StartNewSingleplayerRun(
            character,
            shouldSave: false,
            acts,
            Array.Empty<ModifierModel>(),
            SeedHelper.CanonicalizeSeed(seed),
            GameMode.Standard,
            ascension));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting singleplayer run as {SafeGetText(() => character.Title) ?? character.Id.Entry}.",
            ["character"] = character.Id.Entry,
            ["ascension"] = ascension,
            ["seed"] = SeedHelper.CanonicalizeSeed(seed)
        };
    }

    private static Dictionary<string, object?> ExecuteDevEnterRoom(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");

        string roomTypeValue = GetString(data, "room_type", "monster");
        if (!Enum.TryParse<RoomType>(roomTypeValue, ignoreCase: true, out var roomType))
            return Error($"Unknown room_type '{roomTypeValue}'.");

        TaskHelper.RunSafely(RunManager.Instance.EnterRoomDebug(
            roomType,
            MapPointType.Unassigned,
            model: null,
            showTransition: false));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Entering debug room: {roomType}."
        };
    }

    private static Dictionary<string, object?> ExecuteDevLoadCurrentRunSave()
    {
        if (NGame.Instance == null)
            return Error("NGame.Instance is not available yet.");
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress. Return to main menu before loading a current run save.");

        var loaded = SaveManager.Instance.LoadRunSave();
        if (!loaded.Success || loaded.SaveData == null)
            return Error($"Failed to load current run save: {loaded.Status} {loaded.ErrorMessage}");

        try
        {
            var save = loaded.SaveData;
            var runState = RunState.FromSerializable(save);
            TaskHelper.RunSafely(LoadCurrentRunSaveAsync(runState, save));
        }
        catch (Exception ex)
        {
            return Error($"Load current run failed: {ex.GetBaseException().Message}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Requested loading current_run.save using the game's saved-run path.",
            ["schema_version"] = loaded.SaveData.SchemaVersion,
            ["ascension"] = loaded.SaveData.Ascension,
            ["current_act_index"] = loaded.SaveData.CurrentActIndex,
            ["next_step"] = "Poll get_game_state until the run leaves the menu."
        };
    }

    private static async Task LoadCurrentRunSaveAsync(RunState runState, SerializableRun save)
    {
        if (NGame.Instance == null)
            throw new InvalidOperationException("NGame.Instance is not available.");

        await RunManager.Instance.SetUpSavedSinglePlayer(runState, save);
        NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await NGame.Instance.LoadRun(runState, save.PreFinishedRoom);
    }

    private static Dictionary<string, object?> ExecuteDevValidateCurrentRunSave()
    {
        var loaded = SaveManager.Instance.LoadRunSave();
        if (!loaded.Success || loaded.SaveData == null)
            return Error($"Failed to load current run save: {loaded.Status} {loaded.ErrorMessage}");

        var save = loaded.SaveData;
        var players = save.Players ?? new List<SerializablePlayer>();
        var player = players.FirstOrDefault();
        var deck = player?.Deck ?? new List<SerializableCard>();
        var relics = player?.Relics ?? new List<SerializableRelic>();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["schema_version"] = save.SchemaVersion,
            ["ascension"] = save.Ascension,
            ["current_act_index"] = save.CurrentActIndex,
            ["players"] = players.Count,
            ["character_id"] = player == null ? null : Convert.ToString(player.CharacterId),
            ["deck_count"] = deck.Count,
            ["deck"] = deck.Select(card => card.Id?.ToString()).ToList(),
            ["relic_count"] = relics.Count,
            ["relics"] = relics.Select(relic => relic.Id?.ToString()).ToList(),
            ["gold"] = player?.Gold,
            ["current_hp"] = player?.CurrentHp,
            ["max_hp"] = player?.MaxHp,
            ["max_energy"] = player?.MaxEnergy
        };
    }

    private static Dictionary<string, object?> ExecuteDevListScenarioCommands()
    {
        var commandType = typeof(MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd);
        var commands = typeof(DevConsole).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && commandType.IsAssignableFrom(type))
            .Select(type =>
            {
                try { return Activator.CreateInstance(type) as MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd; }
                catch { return null; }
            })
            .Where(command => command != null && AllowedScenarioConsoleCommands.Contains(command.CmdName))
            .OrderBy(command => command!.CmdName, StringComparer.OrdinalIgnoreCase)
            .Select(command => new Dictionary<string, object?>
            {
                ["command"] = command!.CmdName,
                ["args"] = command.Args,
                ["description"] = command.Description,
                ["debug_only"] = command.DebugOnly,
                ["networked"] = command.IsNetworked
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["commands"] = commands,
            ["guardrail"] = "Only scenario-safe dev-console commands are exposed. Commands that open files, delete cloud saves, send Sentry events, or crash the game are blocked."
        };
    }

    private static Dictionary<string, object?> ExecuteDevRunScenarioCommand(Dictionary<string, JsonElement> data)
    {
        string command = GetString(data, "command", "").Trim();
        if (string.IsNullOrWhiteSpace(command))
            return Error("Missing 'command'.");

        string commandName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!AllowedScenarioConsoleCommands.Contains(commandName))
            return Error($"Scenario command '{commandName}' is not allowed.");

        var result = ScenarioDevConsole.Value.ProcessCommand(command);
        var output = new Dictionary<string, object?>
        {
            ["status"] = result.success ? "ok" : "error",
            ["command"] = command,
            ["success"] = result.success,
            ["message"] = result.msg
        };

        if (result.task != null)
        {
            try
            {
                result.task.GetAwaiter().GetResult();
                output["task_status"] = "completed";
            }
            catch (Exception ex)
            {
                output["status"] = "error";
                output["success"] = false;
                output["task_status"] = "failed";
                output["task_error"] = ex.GetBaseException().Message;
            }
        }

        return output;
    }

    private static Dictionary<string, object?> ExecuteDevConfigureRunDeck(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");
        if (CombatManager.Instance.IsInProgress)
            return Error("Cannot configure the run deck after combat has started. Configure deck first, then enter combat.");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("Run state is unavailable.");

        var player = LocalContext.GetMe(runState);
        if (player == null)
            return Error("Player is unavailable.");

        var deckCards = GetStringArray(data, "deck");
        if (deckCards.Count == 0)
            return Error("Missing non-empty 'deck'.");

        ClearPile(player.Deck);
        var added = AddCardsToDeck(runState, player, deckCards);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Configured run deck before combat.",
            ["deck"] = BuildCardList(player.Deck.Cards),
            ["added"] = added,
            ["next_step"] = "Enter a debug Monster room so the game initializes combat cards from this deck."
        };
    }

    private static Dictionary<string, object?> ExecuteDevReplaceRunDeckAndSave(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("Run state is unavailable.");

        var player = LocalContext.GetMe(runState);
        if (player == null)
            return Error("Player is unavailable.");

        var deckCards = GetStringArray(data, "deck");
        if (deckCards.Count == 0)
            return Error("Missing non-empty 'deck'.");

        bool updateCombatPiles = GetBool(data, "update_combat_piles", true);
        bool persist = GetBool(data, "persist", true);

        ClearPile(player.Deck);
        var added = AddCardsToDeck(runState, player, deckCards);

        var updatedPiles = false;
        if (updateCombatPiles && player.PlayerCombatState != null)
        {
            ClearPile(player.PlayerCombatState.Hand);
            ClearPile(player.PlayerCombatState.DrawPile);
            ClearPile(player.PlayerCombatState.DiscardPile);
            ClearPile(player.PlayerCombatState.ExhaustPile);

            var cards = player.Deck.Cards.ToList();
            for (int i = 0; i < cards.Count; i++)
            {
                var destination = i < 5
                    ? player.PlayerCombatState.Hand
                    : player.PlayerCombatState.DrawPile;
                destination.AddInternal(cards[i], destination.Cards.Count, true);
            }
            updatedPiles = true;
        }

        var saveStatus = "not_requested";
        if (persist)
        {
            try
            {
                TaskHelper.RunSafely(SaveManager.Instance.SaveRun(null));
                saveStatus = "requested";
            }
            catch (Exception ex)
            {
                saveStatus = $"error: {ex.GetBaseException().Message}";
            }
        }

        return new Dictionary<string, object?>
        {
            ["status"] = saveStatus.StartsWith("error:", StringComparison.Ordinal) ? "error" : "ok",
            ["message"] = "Replaced the live run deck through the in-game RunState.",
            ["deck"] = BuildCardList(player.Deck.Cards),
            ["added"] = added,
            ["updated_combat_piles"] = updatedPiles,
            ["save_status"] = saveStatus,
            ["next_step"] = persist
                ? "Poll validate_current_run_save until the saved deck reflects this live deck."
                : "Call this again with persist=true to write through the game's SaveManager."
        };
    }

    private static Dictionary<string, object?> ExecuteDevConfigureTestCombat(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");
        if (!CombatManager.Instance.IsInProgress)
            return Error("No combat in progress. Call start_singleplayer_run and enter_debug_room first.");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("Run state is unavailable.");

        var player = LocalContext.GetMe(runState);
        if (player?.PlayerCombatState == null)
            return Error("Player combat state is unavailable.");

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return Error("Combat state is unavailable.");

        int enemyHp = GetInt(data, "enemy_hp", 999);
        bool clearHand = GetBool(data, "clear_hand", true);
        bool clearDraw = GetBool(data, "clear_draw", true);
        bool clearDiscard = GetBool(data, "clear_discard", true);
        bool clearExhaust = GetBool(data, "clear_exhaust", true);

        var handCards = GetStringArray(data, "hand");
        var drawCards = GetStringArray(data, "draw_pile");
        var discardCards = GetStringArray(data, "discard_pile");
        var exhaustCards = GetStringArray(data, "exhaust_pile");
        var deckCards = GetStringArray(data, "deck");
        if (deckCards.Count > 0)
            return Error("Deck changes must be applied before combat starts. Use dev_configure_run_deck, then enter combat, then configure combat piles.");
        var combatCardPool = ExtractCombatCardPool(player.PlayerCombatState);
        if (clearHand) ClearPile(player.PlayerCombatState.Hand);
        if (clearDraw) ClearPile(player.PlayerCombatState.DrawPile);
        if (clearDiscard) ClearPile(player.PlayerCombatState.DiscardPile);
        if (clearExhaust) ClearPile(player.PlayerCombatState.ExhaustPile);

        var added = new Dictionary<string, object?>
        {
            ["deck"] = BuildCardList(player.Deck.Cards),
            ["hand"] = MoveCardsFromPool(combatCardPool, player.PlayerCombatState.Hand, handCards),
            ["draw_pile"] = MoveCardsFromPool(combatCardPool, player.PlayerCombatState.DrawPile, drawCards),
            ["discard_pile"] = MoveCardsFromPool(combatCardPool, player.PlayerCombatState.DiscardPile, discardCards),
            ["exhaust_pile"] = MoveCardsFromPool(combatCardPool, player.PlayerCombatState.ExhaustPile, exhaustCards)
        };

        if (TryGetNullableInt(data, "energy", out var energy) && energy.HasValue)
            player.PlayerCombatState.Energy = energy.Value;
        if (TryGetNullableInt(data, "stars", out var stars) && stars.HasValue)
            player.PlayerCombatState.Stars = stars.Value;

        var enemies = new List<Dictionary<string, object?>>();
        var livingEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();
        foreach (var enemy in livingEnemies)
        {
            SetCreatureHp(enemy, enemyHp);
            enemies.Add(new Dictionary<string, object?>
            {
                ["name"] = SafeGetText(() => enemy.Monster?.Title),
                ["combat_id"] = enemy.CombatId,
                ["hp"] = enemy.CurrentHp,
                ["max_hp"] = enemy.MaxHp
            });
        }

        var playerPowers = ApplyPowerSpecs(data, "player_powers", [player.Creature]);
        var enemyPowers = ApplyPowerSpecs(data, "enemy_powers", livingEnemies);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Configured current combat for deterministic test validation.",
            ["default_fixture"] = "single durable early/debug monster, high HP, controlled piles",
            ["enemy_hp"] = enemyHp,
            ["energy"] = player.PlayerCombatState.Energy,
            ["stars"] = player.PlayerCombatState.Stars,
            ["enemies"] = enemies,
            ["added"] = added,
            ["player_powers"] = playerPowers,
            ["enemy_powers"] = enemyPowers,
            ["next_step"] = "Call get_game_state, then capture target-visible screenshot evidence."
        };
    }

    private static void ClearPile(CardPile pile)
    {
        foreach (var card in pile.Cards.ToList())
            pile.RemoveInternal(card, true);
    }

    private static List<CardModel> ExtractCombatCardPool(PlayerCombatState playerCombatState)
        => new[]
        {
            playerCombatState.Hand,
            playerCombatState.DrawPile,
            playerCombatState.DiscardPile,
            playerCombatState.ExhaustPile
        }
        .SelectMany(pile => pile.Cards.ToList())
        .ToList();

    private static void SetCreatureHp(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, int hp)
    {
        var type = creature.GetType();
        type.GetMethod("SetMaxHpInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(creature, new object[] { (decimal)hp });
        type.GetMethod("SetCurrentHpInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(creature, new object[] { (decimal)hp });
    }

    private static List<Dictionary<string, object?>> AddCardsToDeck(
        MegaCrit.Sts2.Core.Runs.RunState runState,
        Player player,
        IReadOnlyList<string> cardIds)
    {
        var added = new List<Dictionary<string, object?>>();
        foreach (var cardId in cardIds)
        {
            var canonical = FindCardById(cardId);
            if (canonical == null)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "not_found"
                });
                continue;
            }

            var before = player.Deck.Cards.Count;
            runState.AddCard(canonical, player);
            var card = player.Deck.Cards.Count > before
                ? player.Deck.Cards[player.Deck.Cards.Count - 1]
                : player.Deck.Cards.LastOrDefault(c => c.Id.Entry.Equals(canonical.Id.Entry, StringComparison.OrdinalIgnoreCase)) ?? canonical;
            added.Add(new Dictionary<string, object?>
            {
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["status"] = "ok"
            });
        }
        return added;
    }

    private static List<Dictionary<string, object?>> MoveExistingCardsToPile(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        CardPile destination,
        IReadOnlyList<string> cardIds)
    {
        var added = new List<Dictionary<string, object?>>();
        var playerCombatState = player.PlayerCombatState;
        if (playerCombatState == null)
        {
            foreach (var cardId in cardIds)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "no_player_combat_state"
                });
            }
            return added;
        }

        var sources = new[]
        {
            playerCombatState.Hand,
            playerCombatState.DrawPile,
            playerCombatState.DiscardPile,
            playerCombatState.ExhaustPile
        };

        foreach (var cardId in cardIds)
        {
            var match = FindAndRemoveExistingCombatCard(sources, cardId);
            if (match == null)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "not_found"
                });
                continue;
            }

            destination.AddInternal(match, destination.Cards.Count, true);
            added.Add(new Dictionary<string, object?>
            {
                ["id"] = match.Id.Entry,
                ["name"] = SafeGetText(() => match.Title),
                ["status"] = "ok"
            });
        }
        return added;
    }

    private static List<Dictionary<string, object?>> MoveCardsFromPool(
        List<CardModel> pool,
        CardPile destination,
        IReadOnlyList<string> cardIds)
    {
        var added = new List<Dictionary<string, object?>>();
        foreach (var cardId in cardIds)
        {
            var match = FindAndRemovePooledCombatCard(pool, cardId);
            if (match == null)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "not_found"
                });
                continue;
            }

            destination.AddInternal(match, destination.Cards.Count, true);
            added.Add(new Dictionary<string, object?>
            {
                ["id"] = match.Id.Entry,
                ["name"] = SafeGetText(() => match.Title),
                ["status"] = "ok"
            });
        }
        return added;
    }

    private static CardModel? FindAndRemoveExistingCombatCard(IEnumerable<CardPile> sources, string cardId)
    {
        foreach (var source in sources)
        {
            var match = source.Cards.FirstOrDefault(card =>
                card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
                || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false));
            if (match == null) continue;

            source.RemoveInternal(match, true);
            return match;
        }
        return null;
    }

    private static CardModel? FindAndRemovePooledCombatCard(List<CardModel> pool, string cardId)
    {
        var match = pool.FirstOrDefault(card =>
            card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
            || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false));
        if (match == null) return null;

        pool.Remove(match);
        return match;
    }

    private static List<Dictionary<string, object?>> BuildCardList(IEnumerable<CardModel> cards)
        => cards.Select(card => new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title)
        }).ToList();

    private static CardModel? FindDeckCardById(Player player, string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        return player.Deck.Cards.FirstOrDefault(card =>
            card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
            || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static CardModel? FindCardById(string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        foreach (var card in GetCatalogCards().OfType<CardModel>())
        {
            if (card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
                || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false))
                return card;
        }
        return null;
    }

    private static List<Dictionary<string, object?>> ApplyPowerSpecs(
        Dictionary<string, JsonElement> data,
        string key,
        IReadOnlyList<Creature> targets)
    {
        var result = new List<Dictionary<string, object?>>();
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var spec in elem.EnumerateArray())
        {
            if (spec.ValueKind != JsonValueKind.Object)
                continue;

            string power = GetString(spec, "power", "");
            int amount = GetInt(spec, "amount", 1);
            int targetIndex = GetInt(spec, "target_index", -1);
            var selectedTargets = targetIndex >= 0 && targetIndex < targets.Count
                ? new[] { targets[targetIndex] }
                : targets;

            foreach (var target in selectedTargets)
            {
                result.Add(ApplyPower(target, power, amount));
            }
        }
        return result;
    }

    private static Dictionary<string, object?> ApplyPower(Creature target, string powerName, int amount)
    {
        var powerType = FindPowerType(powerName);
        if (powerType == null)
            return new Dictionary<string, object?>
            {
                ["power"] = powerName,
                ["status"] = "not_found"
            };

        if (Activator.CreateInstance(powerType) is not PowerModel power)
            return new Dictionary<string, object?>
            {
                ["power"] = powerName,
                ["status"] = "create_failed"
            };

        try
        {
            power.ApplyInternal(target, amount, true);
            return new Dictionary<string, object?>
            {
                ["power"] = powerType.Name,
                ["target"] = SafeGetText(() => target.Monster?.Title) ?? "player",
                ["amount"] = amount,
                ["status"] = "ok"
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["power"] = powerType.Name,
                ["amount"] = amount,
                ["status"] = "error",
                ["error"] = ex.Message
            };
        }
    }

    private static Type? FindPowerType(string powerName)
    {
        if (string.IsNullOrWhiteSpace(powerName)) return null;
        string normalized = NormalizeCatalogKey(powerName);
        if (!normalized.EndsWith("power", StringComparison.Ordinal))
            normalized += "power";

        return typeof(PowerModel).Assembly.GetTypes()
            .FirstOrDefault(type =>
                typeof(PowerModel).IsAssignableFrom(type)
                && !type.IsAbstract
                && NormalizeCatalogKey(type.Name) == normalized);
    }

    private static string GetString(Dictionary<string, JsonElement> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return fallback;
        var value = elem.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(Dictionary<string, JsonElement> data, string key, int fallback)
        => data.TryGetValue(key, out var elem) && elem.TryGetInt32(out var value) ? value : fallback;

    private static int GetInt(JsonElement data, string key, int fallback)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var elem)
           && elem.TryGetInt32(out var value)
            ? value
            : fallback;

    private static string GetString(JsonElement data, string key, string fallback)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return fallback;
        var value = elem.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool TryGetNullableInt(Dictionary<string, JsonElement> data, string key, out int? value)
    {
        value = null;
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return false;
        if (!elem.TryGetInt32(out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool GetBool(Dictionary<string, JsonElement> data, string key, bool fallback)
        => data.TryGetValue(key, out var elem) && elem.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? elem.GetBoolean()
            : fallback;

    private static bool TryResolveCardHolder(
        string surface,
        int index,
        string cardId,
        string cardName,
        out NCardHolder? holder,
        out IReadOnlyList<NCardHolder> holders,
        out int resolvedIndex,
        out string error)
    {
        holder = null;
        holders = Array.Empty<NCardHolder>();
        resolvedIndex = index;
        error = "";

        if (!TryResolveCardHolders(surface, out holders, out error))
            return false;

        if (!string.IsNullOrWhiteSpace(cardId) || !string.IsNullOrWhiteSpace(cardName))
        {
            var matches = holders
                .Select((candidate, i) => new { Holder = candidate, Index = i })
                .Where(item => CardHolderMatches(item.Holder, cardId, cardName))
                .ToList();
            if (matches.Count == 0)
            {
                error = $"No {surface} card matched card_id '{cardId}' or card_name '{cardName}'. Available cards: {FormatCardHolderList(holders)}.";
                return false;
            }
            if (matches.Count > 1)
            {
                var indexedMatch = matches.FirstOrDefault(m => m.Index == index);
                if (indexedMatch != null)
                {
                    holder = indexedMatch.Holder;
                    resolvedIndex = indexedMatch.Index;
                    return true;
                }

                error = $"Multiple {surface} cards matched card_id '{cardId}' or card_name '{cardName}'. Matching indices: {string.Join(", ", matches.Select(m => m.Index))}. Use card_index to disambiguate.";
                return false;
            }

            holder = matches[0].Holder;
            resolvedIndex = matches[0].Index;
            return true;
        }

        if (index < 0 || index >= holders.Count)
        {
            error = $"card_index {index} out of range for {surface} ({holders.Count} cards available).";
            return false;
        }

        holder = holders[index];
        return true;
    }

    private static bool TryResolveCardHolders(
        string surface,
        out IReadOnlyList<NCardHolder> holders,
        out string error)
    {
        holders = Array.Empty<NCardHolder>();
        error = "";

        switch (surface)
        {
            case "hand":
                var hand = NPlayerHand.Instance;
                if (hand == null)
                {
                    error = "Player hand is not available.";
                    return false;
                }
                holders = hand.ActiveHolders.Cast<NCardHolder>().ToList();
                return true;

            case "card_select":
            case "deck":
            case "grid":
                var selectOverlay = NOverlayStack.Instance?.Peek();
                if (selectOverlay is NCardGridSelectionScreen gridScreen)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(gridScreen).Cast<NCardHolder>().ToList();
                    return true;
                }
                if (selectOverlay is NChooseACardSelectionScreen chooseScreen)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(chooseScreen).Cast<NCardHolder>().ToList();
                    return true;
                }
                error = "No card selection grid is open.";
                return false;

            case "card_reward":
            case "reward":
                var rewardOverlay = NOverlayStack.Instance?.Peek();
                if (rewardOverlay is not NCardRewardSelectionScreen rewardScreen)
                {
                    error = "Card reward selection screen is not open.";
                    return false;
                }
                holders = FindAllSortedByPosition<NCardHolder>(rewardScreen);
                return true;

            default:
                error = $"Unknown surface '{surface}'. Expected hand, card_select, deck, grid, or card_reward.";
                return false;
        }
    }

    private static bool CardHolderMatches(NCardHolder holder, string cardId, string cardName)
    {
        if (!string.IsNullOrWhiteSpace(cardId))
        {
            var query = NormalizeCardLookupValue(cardId);
            var id = NormalizeCardLookupValue(holder.CardModel?.Id.Entry ?? "");
            if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(cardName))
        {
            var query = NormalizeCardLookupValue(cardName);
            var title = NormalizeCardLookupValue(SafeGetText(() => holder.CardModel?.Title) ?? "");
            if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeCardLookupValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["CARD.".Length..];
        return normalized.Replace(" ", "_").Replace("-", "_");
    }

    private static string FormatCardHolderList(IReadOnlyList<NCardHolder> holders)
        => string.Join(", ", holders.Select((h, i) =>
            $"{i}:{h.CardModel?.Id.Entry ?? "unknown"}:{SafeGetText(() => h.CardModel?.Title) ?? "unknown"}"));

    private static List<Dictionary<string, object?>> BuildVisibleCardList(IReadOnlyList<NCardHolder> holders)
        => holders.Select((h, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["card_id"] = h.CardModel?.Id.Entry,
            ["card_name"] = SafeGetText(() => h.CardModel?.Title) ?? "unknown",
            ["global_position"] = new Dictionary<string, object?>
            {
                ["x"] = h.GlobalPosition.X,
                ["y"] = h.GlobalPosition.Y
            }
        }).ToList();

    private static IReadOnlyList<string> GetStringArray(Dictionary<string, JsonElement> data, string key)
    {
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return elem.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }
}
