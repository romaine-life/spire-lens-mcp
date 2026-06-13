using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

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
    private static volatile bool _currentRunLoadInProgress;
    private static string? _currentRunLoadError;
    private static DateTime _currentRunLoadStartedAtUtc;

    private static bool IsDevAction(string action) => action.StartsWith("dev_", StringComparison.Ordinal);

    private static Dictionary<string, object?> ExecuteDevAction(string action, Dictionary<string, JsonElement> data)
        => action switch
        {
            "dev_reload_spirelens_core" => ExecuteDevReloadSpireLensCore(),
            "dev_start_singleplayer_run" => ExecuteDevStartSingleplayerRun(data),
            "dev_list_scenario_commands" => ExecuteDevListScenarioCommands(),
            "dev_run_scenario_command" => ExecuteDevRunScenarioCommand(data),
            "dev_get_save_context" => ExecuteDevGetSaveContext(),
            "dev_validate_current_run_save" => ExecuteDevValidateCurrentRunSave(),
            "dev_load_current_run_save" => ExecuteDevLoadCurrentRunSave(),
            "dev_configure_run_deck" => ExecuteDevConfigureRunDeck(data),
            "dev_replace_run_deck_and_save" => ExecuteDevReplaceRunDeckAndSave(data),
            "dev_enter_room" => ExecuteDevEnterRoom(data),
            "dev_configure_live_combat" => ExecuteDevConfigureLiveCombat(data),
            "dev_refresh_combat_view" => ExecuteDevRefreshCombatView(),
            "dev_set_spirelens_view_stats_enabled" => ExecuteDevSetSpireLensViewStatsEnabled(data),
            "dev_open_card_pile" => ExecuteDevOpenCardPile(data),
            "dev_close_card_pile" => ExecuteDevCloseCardPile(),
            "dev_list_visible_cards" => ExecuteDevListVisibleCards(data),
            "dev_show_card_tooltip" => ExecuteDevShowCardTooltip(data),
            "dev_list_visible_relics" => ExecuteDevListVisibleRelics(data),
            "dev_show_relic_tooltip" => ExecuteDevShowRelicTooltip(data),
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

    private static Dictionary<string, object?> ExecuteDevGetSaveContext()
    {
        var saveManager = SaveManager.Instance;
        if (saveManager == null)
            return Error("SaveManager.Instance is not available.");

        var runSaveManager = GetPrivateFieldValue(saveManager, "_runSaveManager");
        var saveStore = GetPrivateFieldValue(saveManager, "_saveStore")
            ?? (runSaveManager == null ? null : GetPrivateFieldValue(runSaveManager, "_saveStore"));

        string? currentRunSavePath = GetStringPropertyValue(runSaveManager, "CurrentRunSavePath");
        string? currentMultiplayerRunSavePath = GetStringPropertyValue(runSaveManager, "CurrentMultiplayerRunSavePath");
        string? currentRunSaveFullPath = GetFullSavePath(saveStore, currentRunSavePath);
        string? currentMultiplayerRunSaveFullPath = GetFullSavePath(saveStore, currentMultiplayerRunSavePath);

        int? currentProfileId = null;
        try
        {
            currentProfileId = saveManager.CurrentProfileId;
        }
        catch
        {
            currentProfileId = null;
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["profile_initialized"] = currentProfileId.HasValue,
            ["current_profile_id"] = currentProfileId,
            ["has_run_save"] = SafeValue(() => saveManager.HasRunSave),
            ["has_multiplayer_run_save"] = SafeValue(() => saveManager.HasMultiplayerRunSave),
            ["current_run_save_path"] = currentRunSavePath,
            ["current_run_save_full_path"] = currentRunSaveFullPath,
            ["current_run_save_exists"] = currentRunSaveFullPath == null ? null : File.Exists(currentRunSaveFullPath),
            ["current_multiplayer_run_save_path"] = currentMultiplayerRunSavePath,
            ["current_multiplayer_run_save_full_path"] = currentMultiplayerRunSaveFullPath,
            ["current_multiplayer_run_save_exists"] = currentMultiplayerRunSaveFullPath == null ? null : File.Exists(currentMultiplayerRunSaveFullPath),
            ["save_store_type"] = saveStore?.GetType().FullName,
            ["run_save_manager_type"] = runSaveManager?.GetType().FullName,
            ["source"] = "SaveManager.Instance._runSaveManager.CurrentRunSavePath via ISaveStore.GetFullPath"
        };
    }

    private static object? SafeValue<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static object? GetPrivateFieldValue(object? instance, string fieldName)
    {
        if (instance == null)
            return null;

        return instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static string? GetStringPropertyValue(object? instance, string propertyName)
    {
        if (instance == null)
            return null;

        return instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance) as string;
    }

    private static string? GetFullSavePath(object? saveStore, string? savePath)
    {
        if (saveStore == null || string.IsNullOrWhiteSpace(savePath))
            return ResolveGameUserPath(savePath);

        try
        {
            var method = saveStore.GetType().GetMethod("GetFullPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fullPath = method?.Invoke(saveStore, new object[] { savePath });
            return ResolveGameUserPath(fullPath as string ?? savePath);
        }
        catch
        {
            return ResolveGameUserPath(savePath);
        }
    }

    private static string? ResolveGameUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        if (!path.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
            return path;

        string relative = path["user://".Length..]
            .TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SlayTheSpire2", relative);
    }

    private static Dictionary<string, object?> ExecuteDevSetSpireLensViewStatsEnabled(Dictionary<string, JsonElement> data)
    {
        bool enabled = GetBool(data, "enabled", true);
        bool verboseHandStats = GetBool(data, "verbose_hand_stats", true);
        var bridgeType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SpireLens.Loader.RuntimeOptionsBridge", throwOnError: false))
            .FirstOrDefault(t => t != null);

        if (bridgeType == null)
            return Error("SpireLens runtime options bridge was not found in the current AppDomain.");

        var setMethod = bridgeType.GetMethod("SetViewStatsToggleEnabled", BindingFlags.Public | BindingFlags.Static);
        if (setMethod == null)
            return Error("SpireLens runtime options bridge does not expose SetViewStatsToggleEnabled(bool).");

        setMethod.Invoke(null, new object[] { enabled });

        var setVerboseMethod = bridgeType.GetMethod("SetVerboseHandStatsEnabled", BindingFlags.Public | BindingFlags.Static);
        if (setVerboseMethod != null)
            setVerboseMethod.Invoke(null, new object[] { verboseHandStats });

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"SpireLens View Stats set to {enabled}; verbose hand stats set to {verboseHandStats}.",
            ["enabled"] = enabled,
            ["verbose_hand_stats"] = verboseHandStats
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

    private static Dictionary<string, object?> ExecuteDevShowRelicTooltip(Dictionary<string, JsonElement> data)
    {
        string surface = GetString(data, "surface", "player_relic_bar").ToLowerInvariant();
        int index = GetInt(data, "relic_index", 0);
        string relicId = GetString(data, "relic_id", "");
        string relicNameQuery = GetString(data, "relic_name", "");

        if (!TryResolveRelicHolder(surface, index, relicId, relicNameQuery, out var holder, out var holders, out var resolvedIndex, out var error))
            return Error(error);

        var relicModel = GetRelicHolderModel(holder!);
        var relicName = SafeGetText(() => GetCatalogMemberValue(relicModel, "Title")) ?? "unknown";
        try
        {
            ClearVisibleRelicHoverTips(holders);
            InvokeRelicHolderFocus(holder!);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException?.GetBaseException() ?? ex.GetBaseException();
            return Error($"Relic holder tooltip invocation failed for {surface}[{resolvedIndex}] {relicName}: {inner.GetType().Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            return Error($"Relic holder tooltip invocation failed for {surface}[{resolvedIndex}] {relicName}: {ex.GetBaseException().Message}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Showing hover tooltip for {surface}[{resolvedIndex}]: {relicName}.",
            ["surface"] = surface,
            ["relic_index"] = resolvedIndex,
            ["relic_id"] = GetCatalogEntryId(relicModel),
            ["relic_name"] = relicName,
            ["visible_relics"] = BuildVisibleRelicList(holders)
        };
    }

    private static Dictionary<string, object?> ExecuteDevListVisibleRelics(Dictionary<string, JsonElement> data)
    {
        string surface = GetString(data, "surface", "player_relic_bar").ToLowerInvariant();
        if (!TryResolveRelicHolders(surface, out var holders, out var error))
            return Error(error);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["surface"] = surface,
            ["count"] = holders.Count,
            ["relics"] = BuildVisibleRelicList(holders)
        };
    }

    private static Dictionary<string, object?> ExecuteDevOpenCardPile(Dictionary<string, JsonElement> data)
    {
        string pile = NormalizeCardPileSurface(GetString(data, "pile", "deck"));

        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");

        var runState = RunManager.Instance.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null)
            return Error("Could not find local player.");

        switch (pile)
        {
            case "deck":
                NDeckViewScreen.ShowScreen(player);
                break;

            case "draw_pile":
            case "discard_pile":
            case "exhaust_pile":
                if (player.PlayerCombatState == null)
                    return Error($"{pile} can only be opened during combat.");
                var cards = GetCombatPileCards(player, pile);
                if (cards.Count == 0)
                    return Error($"{pile} is empty.");
                var prefs = new CardSelectorPrefs(new LocString("", ToCardPileTitle(pile)), minCount: 0, maxCount: 0)
                {
                    Cancelable = true,
                    RequireManualConfirmation = false,
                    PretendCardsCanBePlayed = true
                };
                NOverlayStack.Instance?.Push(NDeckCardSelectScreen.Create(cards, prefs));
                break;

            default:
                return Error($"Unknown card pile '{pile}'. Expected deck, draw_pile, discard_pile, or exhaust_pile.");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Opened {pile}. Use list_visible_cards(surface=\"{pile}\") or show_card_tooltip(surface=\"{pile}\", ...), then capture_screenshot for evidence.",
            ["surface"] = pile
        };
    }

    private static Dictionary<string, object?> ExecuteDevCloseCardPile()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NCardGridSelectionScreen or NChooseACardSelectionScreen)
        {
            NOverlayStack.Instance!.Remove(overlay);
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Closed the active card pile overlay."
            };
        }

        var deckView = TryFindActiveDeckView();
        if (deckView != null)
        {
            var returnMethod = typeof(NCardsViewScreen).GetMethod(
                "OnReturnButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (returnMethod == null)
                return Error("NCardsViewScreen.OnReturnButtonPressed could not be found.");
            returnMethod.Invoke(deckView, new object?[] { null });
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Closed the active deck view."
            };
        }

        return Error("No card pile or deck view is open.");
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
        if (_currentRunLoadInProgress)
            return Error("A current run save load is already in progress. Poll get_game_state until state_type is not loading.");
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress. Return to main menu before loading a current run save.");

        var loaded = SaveManager.Instance.LoadRunSave();
        if (!loaded.Success || loaded.SaveData == null)
            return Error($"Failed to load current run save: {loaded.Status} {loaded.ErrorMessage}");

        try
        {
            var save = loaded.SaveData;
            var runState = RunState.FromSerializable(save);
            _currentRunLoadError = null;
            _currentRunLoadStartedAtUtc = DateTime.UtcNow;
            _currentRunLoadInProgress = true;
            TaskHelper.RunSafely(TrackCurrentRunLoadAsync(runState, save));
        }
        catch (Exception ex)
        {
            _currentRunLoadInProgress = false;
            _currentRunLoadError = ex.GetBaseException().Message;
            return Error($"Load current run failed: {_currentRunLoadError}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Started loading current_run.save using the game's saved-run path.",
            ["load_in_progress"] = true,
            ["load_started_at_utc"] = _currentRunLoadStartedAtUtc.ToString("o"),
            ["schema_version"] = loaded.SaveData.SchemaVersion,
            ["ascension"] = loaded.SaveData.Ascension,
            ["current_act_index"] = loaded.SaveData.CurrentActIndex,
            ["next_step"] = "Poll get_game_state until state_type is not loading, menu, or unknown."
        };
    }

    private static async Task TrackCurrentRunLoadAsync(RunState runState, SerializableRun save)
    {
        try
        {
            await LoadCurrentRunSaveAsync(runState, save);
        }
        catch (Exception ex)
        {
            _currentRunLoadError = ex.GetBaseException().Message;
        }
        finally
        {
            _currentRunLoadInProgress = false;
        }
    }

    private static async Task LoadCurrentRunSaveAsync(RunState runState, SerializableRun save)
    {
        if (NGame.Instance == null)
            throw new InvalidOperationException("NGame.Instance is not available.");

        await RunManager.Instance.SetUpSavedSingleplayer(runState, save);
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

    private static Dictionary<string, object?> ExecuteDevConfigureLiveCombat(Dictionary<string, JsonElement> data)
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

        int? enemyHp = null;
        if (TryGetNullableInt(data, "enemy_hp", out var requestedEnemyHp))
            enemyHp = requestedEnemyHp;

        if (TryGetNullableInt(data, "energy", out var energy) && energy.HasValue)
            player.PlayerCombatState.Energy = energy.Value;
        if (TryGetNullableInt(data, "stars", out var stars) && stars.HasValue)
            player.PlayerCombatState.Stars = stars.Value;

        var enemies = new List<Dictionary<string, object?>>();
        var livingEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();
        foreach (var enemy in livingEnemies)
        {
            if (enemyHp.HasValue)
                SetCreatureHp(enemy, enemyHp.Value);
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
            ["message"] = "Configured live combat properties.",
            ["enemy_hp"] = enemyHp,
            ["energy"] = player.PlayerCombatState.Energy,
            ["stars"] = player.PlayerCombatState.Stars,
            ["enemies"] = enemies,
            ["player_powers"] = playerPowers,
            ["enemy_powers"] = enemyPowers,
            ["next_step"] = "Use get_game_state to confirm combat properties, then continue with normal gameplay MCP actions."
        };
    }

    private static Dictionary<string, object?> ExecuteDevRefreshCombatView()
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("No combat in progress.");

        var room = NCombatRoom.Instance;
        if (room == null)
            return Error("NCombatRoom.Instance is null; combat UI is not active.");

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return Error("Combat state is unavailable.");

        // Debug-loaded mid-combat saves mutate creature/energy state without raising the
        // CombatStateChanged signal the HUD widgets subscribe to, so their bound values render
        // stale (placeholder HP bars, 0 energy). Re-fire the same handlers the game uses so the
        // widgets repaint from the live model.
        var creatureResults = new List<Dictionary<string, object?>>();
        foreach (var node in room.CreatureNodes.ToList())
        {
            var entry = new Dictionary<string, object?>();
            try
            {
                var entity = node.Entity;
                entry["entity_name"] = SafeGetText(() => entity?.Name);
                entry["entity_hp"] = entity?.CurrentHp;
                entry["entity_max_hp"] = entity?.MaxHp;

                var display = node.GetType()
                    .GetField("_stateDisplay", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(node);
                if (display == null)
                {
                    entry["display"] = "null";
                    creatureResults.Add(entry);
                    continue;
                }

                var displayType = display.GetType();
                var boundCreature = displayType
                    .GetField("_creature", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(display) as Creature;
                entry["display_bound_name"] = SafeGetText(() => boundCreature?.Name);
                entry["display_bound_hp"] = boundCreature?.CurrentHp;
                entry["display_bound_max_hp"] = boundCreature?.MaxHp;
                entry["display_bound_matches_entity"] = ReferenceEquals(boundCreature, entity);

                displayType.GetMethod("OnCombatStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(display, new object[] { combatState });
                displayType.GetMethod("RefreshValues", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(display, null);
                entry["refreshed"] = true;
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException?.GetBaseException() ?? ex.GetBaseException();
                entry["refreshed"] = false;
                entry["error"] = $"{inner.GetType().Name}: {inner.Message}";
            }
            catch (Exception ex)
            {
                entry["refreshed"] = false;
                entry["error"] = ex.GetBaseException().Message;
            }
            creatureResults.Add(entry);
        }

        var energyResult = new Dictionary<string, object?> { ["status"] = "not_available" };
        try
        {
            var ui = room.Ui;
            var energyCounter = ui?.GetType()
                .GetField("_energyCounter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui);
            if (energyCounter != null)
            {
                var ecType = energyCounter.GetType();
                ecType.GetMethod("OnCombatStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(energyCounter, new object[] { combatState });
                ecType.GetMethod("RefreshLabel", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(energyCounter, null);
                energyResult["status"] = "ok";
            }
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException?.GetBaseException() ?? ex.GetBaseException();
            energyResult["status"] = "error";
            energyResult["error"] = $"{inner.GetType().Name}: {inner.Message}";
        }
        catch (Exception ex)
        {
            energyResult["status"] = "error";
            energyResult["error"] = ex.GetBaseException().Message;
        }

        var handResult = RefreshVisibleHand(combatState);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Re-fired combat-HUD refresh on creature displays, energy counter, and hand.",
            ["creatures"] = creatureResults,
            ["energy"] = energyResult,
            ["hand"] = handResult,
            ["next_step"] = "Capture a screenshot to confirm HP bars and energy now match get_game_state."
        };
    }

    private static Dictionary<string, object?> RefreshVisibleHand(CombatState combatState)
    {
        var result = new Dictionary<string, object?> { ["status"] = "not_available" };
        var hand = NPlayerHand.Instance;
        if (hand == null)
            return result;

        try
        {
            typeof(NPlayerHand)
                .GetMethod("OnCombatStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(hand, new object[] { combatState });
            hand.ForceRefreshCardIndices();
            typeof(NPlayerHand)
                .GetMethod("RefreshLayout", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(hand, null);

            result["status"] = "ok";
            result["visible_count"] = hand.ActiveHolders.Count;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException?.GetBaseException() ?? ex.GetBaseException();
            result["status"] = "error";
            result["error"] = $"{inner.GetType().Name}: {inner.Message}";
        }
        catch (Exception ex)
        {
            result["status"] = "error";
            result["error"] = ex.GetBaseException().Message;
        }

        return result;
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

            case "draw":
            case "draw_pile":
            case "discard":
            case "discard_pile":
            case "exhaust":
            case "exhaust_pile":
                var pileOverlay = NOverlayStack.Instance?.Peek();
                if (pileOverlay is NCardGridSelectionScreen pileGridScreen)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(pileGridScreen).Cast<NCardHolder>().ToList();
                    return true;
                }
                if (pileOverlay is NChooseACardSelectionScreen pileChooseScreen)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(pileChooseScreen).Cast<NCardHolder>().ToList();
                    return true;
                }
                error = $"No card pile view is open for surface '{surface}'. Call open_card_pile(pile=\"{NormalizeCardPileSurface(surface)}\") first.";
                return false;

            case "card_select":
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

            case "deck":
                var deckView = TryFindActiveDeckView();
                if (deckView != null)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(deckView).Cast<NCardHolder>().ToList();
                    return true;
                }
                var deckOverlay = NOverlayStack.Instance?.Peek();
                if (deckOverlay is NCardGridSelectionScreen deckGridScreen)
                {
                    holders = FindAllSortedByPosition<NGridCardHolder>(deckGridScreen).Cast<NCardHolder>().ToList();
                    return true;
                }
                error = "No deck view or card selection grid is open. Call open_card_pile(pile=\"deck\") first.";
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
                error = $"Unknown surface '{surface}'. Expected hand, deck, draw_pile, discard_pile, exhaust_pile, card_select, grid, or card_reward.";
                return false;
        }
    }

    private static bool TryResolveRelicHolder(
        string surface,
        int index,
        string relicId,
        string relicName,
        out Control? holder,
        out IReadOnlyList<Control> holders,
        out int resolvedIndex,
        out string error)
    {
        holder = null;
        holders = Array.Empty<Control>();
        resolvedIndex = index;
        error = "";

        if (!TryResolveRelicHolders(surface, out holders, out error))
            return false;

        if (!string.IsNullOrWhiteSpace(relicId) || !string.IsNullOrWhiteSpace(relicName))
        {
            var matches = holders
                .Select((candidate, i) => new { Holder = candidate, Index = i })
                .Where(item => RelicHolderMatches(item.Holder, relicId, relicName))
                .ToList();
            if (matches.Count == 0)
            {
                error = $"No {surface} relic matched relic_id '{relicId}' or relic_name '{relicName}'. Available relics: {FormatRelicHolderList(holders)}.";
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

                error = $"Multiple {surface} relics matched relic_id '{relicId}' or relic_name '{relicName}'. Matching indices: {string.Join(", ", matches.Select(m => m.Index))}. Use relic_index to disambiguate.";
                return false;
            }

            holder = matches[0].Holder;
            resolvedIndex = matches[0].Index;
            return true;
        }

        if (index < 0 || index >= holders.Count)
        {
            error = $"relic_index {index} out of range for {surface} ({holders.Count} relics available).";
            return false;
        }

        holder = holders[index];
        return true;
    }

    private static bool TryResolveRelicHolders(
        string surface,
        out IReadOnlyList<Control> holders,
        out string error)
    {
        surface = NormalizeRelicSurface(surface);
        holders = Array.Empty<Control>();
        error = "";

        switch (surface)
        {
            case "player_relic_bar":
            case "relic_bar":
            case "inventory":
                var root = NGame.Instance?.GetTree()?.Root;
                if (root == null)
                {
                    error = "Game root is not available.";
                    return false;
                }
                holders = FindAllSortedByPosition<NRelicInventoryHolder>(root)
                    .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && GetRelicHolderModel(h) != null)
                    .Cast<Control>()
                    .ToList();
                return true;

            case "relic_select":
                var selectOverlay = NOverlayStack.Instance?.Peek();
                if (selectOverlay is not NChooseARelicSelection relicSelection)
                {
                    error = "Relic selection screen is not open.";
                    return false;
                }
                holders = FindAllSortedByPosition<NRelicBasicHolder>(relicSelection)
                    .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && GetRelicHolderModel(h) != null)
                    .Cast<Control>()
                    .ToList();
                return true;

            case "treasure":
                var treasureUI = FindFirst<NTreasureRoom>(((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
                if (treasureUI == null)
                {
                    error = "Treasure room is not open.";
                    return false;
                }

                var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
                if (relicCollection?.Visible != true)
                {
                    error = "Relic collection is not visible - chest may not be opened yet.";
                    return false;
                }

                holders = FindAllSortedByPosition<NTreasureRoomRelicHolder>(relicCollection)
                    .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && h.IsEnabled && GetRelicHolderModel(h) != null)
                    .Cast<Control>()
                    .ToList();
                return true;

            default:
                error = $"Unknown relic surface '{surface}'. Expected player_relic_bar, relic_select, or treasure.";
                return false;
        }
    }

    private static string NormalizeRelicSurface(string value)
        => value.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");

    private static bool RelicHolderMatches(Control holder, string relicId, string relicName)
    {
        var model = GetRelicHolderModel(holder);
        if (model == null)
            return false;

        if (!string.IsNullOrWhiteSpace(relicId))
        {
            var query = NormalizeRelicLookupValue(relicId);
            var id = NormalizeRelicLookupValue(GetCatalogEntryId(model) ?? "");
            if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(relicName))
        {
            var query = NormalizeRelicLookupValue(relicName);
            var title = NormalizeRelicLookupValue(SafeGetText(() => GetCatalogMemberValue(model, "Title")) ?? "");
            if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeRelicLookupValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("RELIC.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["RELIC.".Length..];
        return normalized.Replace(" ", "_").Replace("-", "_");
    }

    private static object? GetRelicHolderModel(Control holder)
    {
        var relic = GetCatalogMemberValue(holder, "Relic");
        return GetCatalogMemberValue(relic, "Model");
    }

    private static void InvokeRelicHolderFocus(Control holder)
    {
        var onFocus = holder.GetType().GetMethod("OnFocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (onFocus == null)
            throw new MissingMethodException(holder.GetType().FullName, "OnFocus");

        onFocus.Invoke(holder, null);
    }

    private static void ClearVisibleRelicHoverTips(IEnumerable<Control> holders)
    {
        foreach (var candidate in holders)
        {
            if (!GodotObject.IsInstanceValid(candidate))
                continue;

            var onUnfocus = candidate.GetType().GetMethod("OnUnfocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (onUnfocus == null)
                continue;

            try { onUnfocus.Invoke(candidate, null); }
            catch { }
        }
    }

    private static string NormalizeCardPileSurface(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return normalized switch
        {
            "draw" or "drawpile" or "draw_pile" => "draw_pile",
            "discard" or "discardpile" or "discard_pile" => "discard_pile",
            "exhaust" or "exhaustpile" or "exhaust_pile" => "exhaust_pile",
            "deck" or "full_deck" or "run_deck" => "deck",
            _ => normalized
        };
    }

    private static IReadOnlyList<CardModel> GetCombatPileCards(Player player, string pile)
    {
        var combatState = player.PlayerCombatState;
        if (combatState == null)
            return Array.Empty<CardModel>();

        return pile switch
        {
            "draw_pile" => combatState.DrawPile.Cards.ToList(),
            "discard_pile" => combatState.DiscardPile.Cards.ToList(),
            "exhaust_pile" => combatState.ExhaustPile.Cards.ToList(),
            _ => Array.Empty<CardModel>()
        };
    }

    private static string ToCardPileTitle(string pile)
        => pile switch
        {
            "draw_pile" => "Draw Pile",
            "discard_pile" => "Discard Pile",
            "exhaust_pile" => "Exhaust Pile",
            _ => "Card Pile"
        };

    private static NDeckViewScreen? TryFindActiveDeckView()
    {
        var root = NGame.Instance?.GetTree()?.Root;
        if (root == null)
            return null;

        return FindAll<NDeckViewScreen>(root)
            .Where(screen => GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
            .LastOrDefault();
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

    private static string FormatRelicHolderList(IReadOnlyList<Control> holders)
        => string.Join(", ", holders.Select((h, i) =>
        {
            var model = GetRelicHolderModel(h);
            return $"{i}:{GetCatalogEntryId(model) ?? "unknown"}:{SafeGetText(() => GetCatalogMemberValue(model, "Title")) ?? "unknown"}";
        }));

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

    private static List<Dictionary<string, object?>> BuildVisibleRelicList(IReadOnlyList<Control> holders)
        => holders.Select((h, i) =>
        {
            var model = GetRelicHolderModel(h);
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["relic_id"] = GetCatalogEntryId(model),
                ["relic_name"] = SafeGetText(() => GetCatalogMemberValue(model, "Title")) ?? "unknown",
                ["relic_description"] = SafeGetText(() => GetCatalogMemberValue(model, "DynamicDescription")),
                ["global_position"] = new Dictionary<string, object?>
                {
                    ["x"] = h.GlobalPosition.X,
                    ["y"] = h.GlobalPosition.Y
                }
            };
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
