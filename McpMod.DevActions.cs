using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Mcp;

public static partial class McpMod
{
    private static bool IsDevAction(string action) => action.StartsWith("dev_", StringComparison.Ordinal);

    private static Dictionary<string, object?> ExecuteDevAction(string action, Dictionary<string, JsonElement> data)
        => action switch
        {
            "dev_reload_spirelens_core" => ExecuteDevReloadSpireLensCore(),
            "dev_start_singleplayer_run" => ExecuteDevStartSingleplayerRun(data),
            "dev_enter_room" => ExecuteDevEnterRoom(data),
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

    private static string GetString(Dictionary<string, JsonElement> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return fallback;
        var value = elem.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(Dictionary<string, JsonElement> data, string key, int fallback)
        => data.TryGetValue(key, out var elem) && elem.TryGetInt32(out var value) ? value : fallback;
}
