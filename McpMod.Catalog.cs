using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace SpireLens.Mcp;

public static partial class McpMod
{
    private static void HandleGetCatalog(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var resultTask = RunOnMainThread(BuildCatalogSummary);
            SendJson(response, resultTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Catalog read failed: {ex.Message}");
        }
    }

    private static void HandlePostCatalogAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";
        try
        {
            var resultTask = RunOnMainThread(() => ExecuteCatalogAction(action, parsed));
            SendJson(response, resultTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Catalog action failed: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ExecuteCatalogAction(string action, Dictionary<string, JsonElement> data)
        => action switch
        {
            "lookup_card" => ExecuteCatalogLookupCard(data),
            "list_cards" => ExecuteCatalogListCards(data),
            "lookup_character" => ExecuteCatalogLookupCharacter(data),
            "list_characters" => BuildCatalogCharactersResult(),
            "get_validation_capabilities" => BuildValidationCapabilities(),
            _ => Error($"Unknown catalog action: {action}")
        };

    private static Dictionary<string, object?> BuildCatalogSummary()
    {
        var cards = GetCatalogCards();
        var characters = GetCatalogCharacters();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["card_count"] = cards.Count,
            ["character_count"] = characters.Count,
            ["characters"] = characters.Select(BuildCatalogCharacterInfo).ToList()
        };
    }

    private static Dictionary<string, object?> BuildCatalogCharactersResult()
        => new()
        {
            ["status"] = "ok",
            ["characters"] = GetCatalogCharacters().Select(BuildCatalogCharacterInfo).ToList()
        };

    private static Dictionary<string, object?> BuildValidationCapabilities()
        => new()
        {
            ["status"] = "ok",
            ["version"] = 2,
            ["purpose"] = "Cold metadata describing the live-validation surfaces and MCP tools that later agent phases may use.",
            ["runtime_options"] = new Dictionary<string, object?>
            {
                ["view_stats"] = new Dictionary<string, object?>
                {
                    ["tool"] = "set_spirelens_view_stats_enabled",
                    ["default_enabled_for_validation"] = true,
                    ["notes"] = "Enables SpireLens card-stat tooltips without opening the deck view first."
                },
                ["verbose_hand_stats"] = new Dictionary<string, object?>
                {
                    ["tool"] = "set_spirelens_view_stats_enabled",
                    ["argument"] = "verbose_hand_stats",
                    ["default_enabled_for_validation"] = true,
                    ["notes"] = "Allows in-hand card-stat tooltips to render the full stats body for screenshot evidence. Normal player config defaults this off."
                }
            },
            ["card_surfaces"] = new List<Dictionary<string, object?>>
            {
                BuildValidationSurface("hand", "current combat hand", false, true, true),
                BuildValidationSurface("deck", "full run deck view", true, true, true),
                BuildValidationSurface("draw_pile", "current combat draw pile", true, true, true),
                BuildValidationSurface("discard_pile", "current combat discard pile", true, true, true),
                BuildValidationSurface("exhaust_pile", "current combat exhaust pile", true, true, true),
                BuildValidationSurface("card_select", "active card selection grid", false, true, true),
                BuildValidationSurface("card_reward", "active card reward choices", false, true, true)
            },
            ["recommended_tooltip_evidence_flow"] = new[]
            {
                "set_spirelens_view_stats_enabled(enabled=true, verbose_hand_stats=true)",
                "open_card_pile(pile) when the target is in deck/draw_pile/discard_pile/exhaust_pile",
                "list_visible_cards(surface)",
                "show_card_tooltip(surface, card_id=target_id)",
                "capture_screenshot",
                "close_card_pile() when a pile view was opened"
            },
            ["screenshot_contract"] = new Dictionary<string, object?>
            {
                ["tool"] = "capture_screenshot",
                ["canonical_view"] = "full STS2 game window/client area",
                ["target_visible_required_for_ui_issues"] = true,
                ["text_visible_required_when_issue_claims_tooltip_text"] = true
            },
            ["scenario_setup"] = new Dictionary<string, object?>
            {
                ["preferred_card_availability"] = "Materialize a deterministic scenario save with a small deck of real card ids.",
                ["base_saves"] = new[] { "base_ironclad", "base_silent", "base_defect", "base_regent", "base_necrobinder" },
                ["normal_encounter_default"] = "FUZZY_WURM_CRAWLER_WEAK"
            },
            ["tools"] = BuildValidationToolManifest()
        };

    private static List<Dictionary<string, object?>> BuildValidationToolManifest()
        => new()
        {
            BuildValidationTool(
                "lookup_card",
                "catalog",
                "Resolve a card id, display name, ownership, type, rarity, and ambiguity status from the live STS2 model catalog.",
                mutatesState: false,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: true,
                outputContract: "JSON with status ok|not_found|ambiguous, kind=card, match_count, matches[], and card when exactly one match exists.",
                commonFailures: new[] { "query missing", "not_found", "ambiguous" },
                examples: new[] { "lookup_card(query=\"Make It So\")", "lookup_card(query=\"MAKE_IT_SO\")" }),
            BuildValidationTool(
                "list_cards",
                "catalog",
                "List real card ids from the live catalog using owner/type/query filters. Use for support-card selection in scenario decks.",
                mutatesState: false,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: true,
                outputContract: "JSON with status ok, filters echoed, count, and cards[] containing id/name/type/rarity/description/owners.",
                commonFailures: new[] { "overly narrow filter returns too few cards" },
                examples: new[] { "list_cards(owner=\"REGENT\", type=\"Skill\", limit=20)" }),
            BuildValidationTool(
                "lookup_character",
                "catalog",
                "Resolve a character id/name and card-pool identity from the live STS2 model catalog.",
                mutatesState: false,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: true,
                outputContract: "JSON with status ok|not_found|ambiguous, kind=character, match_count, matches[], and character when exactly one match exists.",
                commonFailures: new[] { "query missing", "not_found", "ambiguous" },
                examples: new[] { "lookup_character(query=\"Regent\")" }),
            BuildValidationTool(
                "get_validation_capabilities",
                "catalog",
                "Return this manifest: available validation surfaces, runtime options, evidence flow, screenshot contract, scenario setup defaults, and per-tool usage metadata.",
                mutatesState: false,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: true,
                outputContract: "JSON with status ok, version, runtime_options, card_surfaces, recommended_tooltip_evidence_flow, screenshot_contract, scenario_setup, tools[].",
                commonFailures: Array.Empty<string>(),
                examples: new[] { "get_validation_capabilities()" }),
            BuildValidationTool(
                "set_spirelens_view_stats_enabled",
                "runtime_option",
                "Enable or disable SpireLens card-stat tooltips and optionally turn on verbose in-hand stats for automation screenshots.",
                mutatesState: true,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok, enabled, verbose_hand_stats.",
                commonFailures: new[] { "SpireLens loader bridge missing", "game not running" },
                examples: new[] { "set_spirelens_view_stats_enabled(enabled=true, verbose_hand_stats=true)" }),
            BuildValidationTool(
                "list_visible_cards",
                "ui_evidence",
                "List card holders currently visible on a tooltip-capable surface so later calls can target by stable card id or index.",
                mutatesState: false,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok, surface, count, cards[] containing index/card_id/card_name/global_position.",
                commonFailures: new[] { "surface not open", "player hand unavailable", "empty pile" },
                examples: new[] { "list_visible_cards(surface=\"hand\")", "list_visible_cards(surface=\"discard_pile\") after open_card_pile(pile=\"discard_pile\")" }),
            BuildValidationTool(
                "show_card_tooltip",
                "ui_evidence",
                "Force the game to create hover tooltips for a visible card holder, preferably selected by card_id.",
                mutatesState: true,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok, surface, card_index, card_id, card_name, visible_cards[].",
                commonFailures: new[] { "target card not visible", "ambiguous duplicate card_id without card_index", "surface not open" },
                examples: new[] { "show_card_tooltip(surface=\"hand\", card_id=\"MAKE_IT_SO\")", "show_card_tooltip(surface=\"deck\", card_id=\"BASH\")" }),
            BuildValidationTool(
                "open_card_pile",
                "ui_navigation",
                "Open a real in-game card grid for deck/draw/discard/exhaust pile evidence. Required before list/show calls on those surfaces.",
                mutatesState: true,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok and surface when the pile view is open.",
                commonFailures: new[] { "no run in progress", "combat pile requested outside combat", "requested combat pile empty" },
                examples: new[] { "open_card_pile(pile=\"deck\")", "open_card_pile(pile=\"exhaust_pile\")" }),
            BuildValidationTool(
                "close_card_pile",
                "ui_navigation",
                "Close the active card pile overlay or deck view opened for MCP inspection.",
                mutatesState: true,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok when a card pile/deck view was closed.",
                commonFailures: new[] { "no card pile or deck view is open" },
                examples: new[] { "close_card_pile()" }),
            BuildValidationTool(
                "capture_screenshot",
                "evidence_capture",
                "Capture the full STS2 game viewport to a PNG under SCREENSHOT_DIR. This is the canonical screenshot evidence path.",
                mutatesState: false,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status/metadata, path, dimensions, and no inline PNG payload.",
                commonFailures: new[] { "game not running", "screenshot directory unavailable", "viewport capture failed" },
                examples: new[] { "capture_screenshot(name=\"issue78-make-it-so-tooltip.png\")" }),
            BuildValidationTool(
                "get_game_state",
                "state_read",
                "Read current STS2 state, including state_type, combat hand/piles/enemies, player stats, map/event/reward screens.",
                mutatesState: false,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "Markdown or JSON. JSON includes state_type and screen-specific state; combat states include hand/draw_pile/discard_pile/exhaust_pile summaries.",
                commonFailures: new[] { "game not running", "transition/loading state", "unexpected menu state" },
                examples: new[] { "get_game_state(format=\"json\")" }),
            BuildValidationTool(
                "materialize_scenario_save",
                "scenario_setup",
                "Create a derived scenario save from a base save by editing stable pre-combat fields such as deck, relics, gold, HP, max energy, and next encounter.",
                mutatesState: true,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok, base, scenario, before/after summaries, bytes, sha256.",
                commonFailures: new[] { "base save missing", "unsupported save shape", "invalid card/relic/encounter id" },
                examples: new[] { "materialize_scenario_save(base_name=\"base_regent\", scenario_name=\"issue_78_make_it_so\", deck=[\"MAKE_IT_SO\",\"DEFEND_REGENT\",\"GATHER_LIGHT\",\"GLOW\"], max_energy=3)" }),
            BuildValidationTool(
                "install_save_as_current",
                "scenario_setup",
                "Install a managed base/scenario save as STS2 current_run.save, writing both AppData and Steam remote mirrors.",
                mutatesState: true,
                requiresGameRunning: false,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok, installed, targets[], target_count, sha256.",
                commonFailures: new[] { "scenario save missing", "save target path unavailable" },
                examples: new[] { "install_save_as_current(name=\"issue_78_make_it_so\", kind=\"scenario\")" }),
            BuildValidationTool(
                "load_current_run_save",
                "scenario_setup",
                "Load STS2 current_run.save through the game's saved-run path after installation and startup.",
                mutatesState: true,
                requiresGameRunning: true,
                requiresCombat: false,
                safeForPlanning: false,
                outputContract: "JSON with status ok or loading/error state for the run-load transition.",
                commonFailures: new[] { "run already in progress", "save validation failure", "called before menu is stable" },
                examples: new[] { "load_current_run_save()" })
        };

    private static Dictionary<string, object?> BuildValidationTool(
        string name,
        string category,
        string description,
        bool mutatesState,
        bool requiresGameRunning,
        bool requiresCombat,
        bool safeForPlanning,
        string outputContract,
        IReadOnlyList<string> commonFailures,
        IReadOnlyList<string> examples)
        => new()
        {
            ["name"] = name,
            ["category"] = category,
            ["description"] = description,
            ["phase_guidance"] = new Dictionary<string, object?>
            {
                ["safe_for_test_planning"] = safeForPlanning,
                ["safe_for_verification"] = true,
                ["implementation_should_use"] = category == "catalog"
            },
            ["mutates_state"] = mutatesState,
            ["requires_game_running"] = requiresGameRunning,
            ["requires_combat"] = requiresCombat,
            ["output_contract"] = outputContract,
            ["common_failures"] = commonFailures,
            ["examples"] = examples
        };

    private static Dictionary<string, object?> BuildValidationSurface(
        string name,
        string description,
        bool requiresOpenCardPile,
        bool supportsListVisibleCards,
        bool supportsShowCardTooltip)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["requires_open_card_pile"] = requiresOpenCardPile,
            ["open_card_pile_argument"] = requiresOpenCardPile ? name : null,
            ["supports_list_visible_cards"] = supportsListVisibleCards,
            ["supports_show_card_tooltip"] = supportsShowCardTooltip,
            ["supports_card_id_lookup"] = supportsShowCardTooltip
        };

    private static Dictionary<string, object?> ExecuteCatalogLookupCharacter(Dictionary<string, JsonElement> data)
    {
        string query = GetString(data, "query", "");
        if (string.IsNullOrWhiteSpace(query))
            return Error("Missing 'query'.");

        var matches = MatchCatalogObjects(GetCatalogCharacters(), query, BuildCatalogCharacterInfo).Take(10).ToList();
        return BuildLookupResult("character", query, matches);
    }

    private static Dictionary<string, object?> ExecuteCatalogLookupCard(Dictionary<string, JsonElement> data)
    {
        string query = GetString(data, "query", "");
        if (string.IsNullOrWhiteSpace(query))
            return Error("Missing 'query'.");

        int maxMatches = Math.Clamp(GetInt(data, "max_matches", 10), 1, 50);
        var matches = MatchCatalogObjects(GetCatalogCards(), query, BuildCatalogCardInfo).Take(maxMatches).ToList();
        return BuildLookupResult("card", query, matches);
    }

    private static Dictionary<string, object?> ExecuteCatalogListCards(Dictionary<string, JsonElement> data)
    {
        string owner = GetString(data, "owner", "");
        string type = GetString(data, "type", "");
        string query = GetString(data, "query", "");
        int limit = Math.Clamp(GetInt(data, "limit", 50), 1, 200);

        string normalizedOwner = NormalizeCatalogKey(owner);
        string normalizedType = NormalizeCatalogKey(type);
        string normalizedQuery = NormalizeCatalogKey(query);

        var cards = GetCatalogCards()
            .Select(BuildCatalogCardInfo)
            .Where(card => MatchesCatalogCardFilter(card, normalizedOwner, normalizedType, normalizedQuery))
            .OrderBy(card => Convert.ToString(card.GetValueOrDefault("name")), StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => Convert.ToString(card.GetValueOrDefault("id")), StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["owner"] = string.IsNullOrWhiteSpace(owner) ? null : owner,
            ["type"] = string.IsNullOrWhiteSpace(type) ? null : type,
            ["query"] = string.IsNullOrWhiteSpace(query) ? null : query,
            ["count"] = cards.Count,
            ["cards"] = cards
        };
    }

    private static bool MatchesCatalogCardFilter(
        Dictionary<string, object?> card,
        string normalizedOwner,
        string normalizedType,
        string normalizedQuery)
    {
        if (!string.IsNullOrWhiteSpace(normalizedType)
            && NormalizeCatalogKey(Convert.ToString(card.GetValueOrDefault("type")) ?? "") != normalizedType)
            return false;

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            string id = NormalizeCatalogKey(Convert.ToString(card.GetValueOrDefault("id")) ?? "");
            string name = NormalizeCatalogKey(Convert.ToString(card.GetValueOrDefault("name")) ?? "");
            if (!id.Contains(normalizedQuery) && !name.Contains(normalizedQuery))
                return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedOwner))
            return true;

        if (card.GetValueOrDefault("owners") is not IEnumerable<object> owners)
            return false;

        foreach (var owner in owners)
        {
            if (owner is not Dictionary<string, object?> ownerInfo)
                continue;

            string ownerId = NormalizeCatalogKey(Convert.ToString(ownerInfo.GetValueOrDefault("id")) ?? "");
            string ownerName = NormalizeCatalogKey(Convert.ToString(ownerInfo.GetValueOrDefault("name")) ?? "");
            string poolId = NormalizeCatalogKey(Convert.ToString(ownerInfo.GetValueOrDefault("card_pool_id")) ?? "");
            string poolName = NormalizeCatalogKey(Convert.ToString(ownerInfo.GetValueOrDefault("card_pool_name")) ?? "");
            if (ownerId == normalizedOwner || ownerName == normalizedOwner || poolId == normalizedOwner || poolName == normalizedOwner)
                return true;
        }

        return false;
    }

    private static Dictionary<string, object?> BuildLookupResult(string kind, string query, List<Dictionary<string, object?>> matches)
    {
        string status = matches.Count switch
        {
            0 => "not_found",
            1 => "ok",
            _ => "ambiguous"
        };

        var result = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["kind"] = kind,
            ["query"] = query,
            ["match_count"] = matches.Count,
            ["matches"] = matches
        };

        if (matches.Count == 1)
            result[kind] = matches[0];
        else if (matches.Count == 0)
            result["error"] = $"No {kind} matched '{query}'.";
        else
            result["error"] = $"{matches.Count} {kind}s matched '{query}'. The issue is ambiguous.";

        return result;
    }

    private static List<Dictionary<string, object?>> MatchCatalogObjects(
        IEnumerable<object> objects,
        string query,
        Func<object, Dictionary<string, object?>> buildInfo)
    {
        string normalizedQuery = NormalizeCatalogKey(query);
        var scored = new List<(int Score, string Name, Dictionary<string, object?> Info)>();

        foreach (var obj in objects)
        {
            string id = GetCatalogEntryId(obj) ?? "";
            string name = SafeGetText(() => GetCatalogMemberValue(obj, "Title")) ?? "";
            string normalizedId = NormalizeCatalogKey(id);
            string normalizedName = NormalizeCatalogKey(name);

            int score = 0;
            if (normalizedId == normalizedQuery) score = 100;
            else if (normalizedName == normalizedQuery) score = 95;
            else if (normalizedId.Contains(normalizedQuery)) score = 70;
            else if (normalizedName.Contains(normalizedQuery)) score = 65;

            if (score > 0)
            {
                var info = buildInfo(obj);
                scored.Add((score, name, info));
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Info)
            .ToList();
    }

    private static List<object> GetCatalogCharacters()
        => ModelDb.AllCharacters.Cast<object>().ToList();

    private static List<object> GetCatalogCards()
    {
        var cards = new List<object>();
        cards.AddRange(GetStaticModelDbSequence("AllCards", "Cards", "CardModels").Where(IsCatalogCardLike));

        foreach (var character in GetCatalogCharacters())
        {
            var cardPool = GetCatalogMemberValue(character, "CardPool");
            cards.AddRange(ExtractCatalogCards(cardPool, maxDepth: 3));
        }

        return DistinctCatalogObjectsById(cards);
    }

    private static Dictionary<string, object?> BuildCatalogCharacterInfo(object character)
    {
        var cardPool = GetCatalogMemberValue(character, "CardPool");
        return new Dictionary<string, object?>
        {
            ["id"] = GetCatalogEntryId(character),
            ["name"] = SafeGetText(() => GetCatalogMemberValue(character, "Title")),
            ["card_pool_id"] = GetCatalogEntryId(cardPool),
            ["card_pool_name"] = SafeGetText(() => GetCatalogMemberValue(cardPool, "Title"))
        };
    }

    private static Dictionary<string, object?> BuildCatalogCardInfo(object card)
    {
        Dictionary<string, object?> info;
        if (card is CardModel cardModel)
        {
            info = BuildCardInfo(cardModel);
            info["target_type"] = cardModel.TargetType.ToString();
        }
        else
        {
            info = new Dictionary<string, object?>
            {
                ["id"] = GetCatalogEntryId(card),
                ["name"] = SafeGetText(() => GetCatalogMemberValue(card, "Title")),
                ["type"] = GetCatalogMemberValue(card, "Type")?.ToString(),
                ["rarity"] = GetCatalogMemberValue(card, "Rarity")?.ToString(),
                ["description"] = SafeGetText(() => GetCatalogMemberValue(card, "Description"))
            };
        }

        string? cardId = Convert.ToString(info.GetValueOrDefault("id"));
        info["owners"] = string.IsNullOrWhiteSpace(cardId)
            ? new List<Dictionary<string, object?>>()
            : GetCatalogCardOwners(cardId).Select(BuildCatalogCharacterInfo).ToList();
        return info;
    }

    private static List<object> GetCatalogCardOwners(string cardId)
    {
        var owners = new List<object>();
        foreach (var character in GetCatalogCharacters())
        {
            var cardPool = GetCatalogMemberValue(character, "CardPool");
            if (ExtractCatalogCards(cardPool, maxDepth: 3).Any(card => string.Equals(GetCatalogEntryId(card), cardId, StringComparison.OrdinalIgnoreCase)))
                owners.Add(character);
        }
        return owners;
    }

    private static IEnumerable<object> GetStaticModelDbSequence(params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            var value = GetCatalogMemberValue(typeof(ModelDb), memberName, isStatic: true);
            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                    if (item != null)
                        yield return item;
            }
        }
    }

    private static List<object> ExtractCatalogCards(object? source, int maxDepth)
    {
        var result = new List<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ExtractCatalogCards(source, maxDepth, result, visited);
        return DistinctCatalogObjectsById(result);
    }

    private static void ExtractCatalogCards(object? source, int depth, List<object> result, HashSet<object> visited)
    {
        if (source == null || depth < 0 || source is string) return;
        if (!visited.Add(source)) return;

        if (IsCatalogCardLike(source))
        {
            result.Add(source);
            return;
        }

        if (source is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                ExtractCatalogCards(item, depth - 1, result, visited);
            return;
        }

        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            if (property.PropertyType == typeof(string)) continue;
            if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && !property.Name.Contains("Card", StringComparison.OrdinalIgnoreCase)) continue;

            object? value;
            try { value = property.GetValue(source); }
            catch { continue; }
            ExtractCatalogCards(value, depth - 1, result, visited);
        }
    }

    private static bool IsCatalogCardLike(object obj)
        => GetCatalogEntryId(obj) != null
           && SafeGetText(() => GetCatalogMemberValue(obj, "Title")) != null
           && GetCatalogMemberValue(obj, "Type") != null;

    private static List<object> DistinctCatalogObjectsById(IEnumerable<object> objects)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<object>();
        foreach (var obj in objects)
        {
            string? id = GetCatalogEntryId(obj);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            result.Add(obj);
        }
        return result;
    }

    private static object? GetCatalogMemberValue(object? source, string memberName, bool isStatic = false)
    {
        if (source == null) return null;
        var type = source as Type ?? source.GetType();
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        try
        {
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(isStatic ? null : source);
            var field = type.GetField(memberName, flags);
            if (field != null)
                return field.GetValue(isStatic ? null : source);
        }
        catch { }
        return null;
    }

    private static string? GetCatalogEntryId(object? source)
    {
        var id = GetCatalogMemberValue(source, "Id");
        var entry = GetCatalogMemberValue(id, "Entry");
        return entry?.ToString() ?? id?.ToString();
    }

    private static string NormalizeCatalogKey(string value)
    {
        var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }
}
