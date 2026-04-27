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
